using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// <see cref="ILogService"/> 的默认实现。
    ///
    /// 写入路径分两条：Debug 级只落盘（高频，进界面会刷屏）；
    /// 其余级别先进内存集合，再由后台线程每 500ms 成批写文件 ——
    /// 每条日志都在 UI 线程上 CreateDirectory + AppendAllText 会让界面明显卡顿。
    /// </summary>
    public sealed class LogService : ILogService, IDisposable
    {
        private const string LogPrefix = "zentwik-";

        /// <summary>
        /// 历史日志文件名前缀。改名前的日志同样要参与留存清理，
        /// 否则它们会永远留在用户目录里，谁都不删。
        /// </summary>
        private static readonly string[] LegacyLogPrefixes = ["zenforge-"];

        private readonly LogOptions _options;

        private readonly IUiDispatcher? _dispatcher;

        private readonly TimeProvider _timeProvider;

        private readonly ObservableCollection<LogEntry> _entries = [];

        private readonly ReadOnlyObservableCollection<LogEntry> _entriesView;

        /// <summary>
        /// 保护 <see cref="_entries"/> 的并发写入。
        ///
        /// UI 模式下写入由 <see cref="IUiDispatcher"/> 封送到 UI 线程，天然串行；
        /// 无 UI 的后台模式（-silent）下，采样线程与轮询线程会并发写同一个集合，
        /// 不加锁就是随机的「集合已被修改」异常。
        /// </summary>
        private readonly object _entriesGate = new();

        private readonly object _fileGate = new();

        private readonly Queue<LogEntry> _pendingFile = new();

        /// <summary>
        /// 落盘线程的唤醒信号。必须是 AutoResetEvent：ManualResetEventSlim 的 Set
        /// 若落在「Wait 已返回、Reset 未执行」的间隙里，信号会被吃掉，那批日志要多等一个周期。
        /// </summary>
        private readonly AutoResetEvent _fileSignal = new(false);

        private readonly ManualResetEvent _stopSignal = new(false);

        private readonly Thread _writer;

        private bool _directoryReady;

        /// <summary>
        /// 日志「代号」。每次清空 +1，写盘线程落盘前核对：
        /// 对不上说明这批已被清掉，直接丢弃，别把旧日志写回刚删掉的文件。
        /// </summary>
        private int _generation;

        private long _countInfo;
        private long _countWarn;
        private long _countError;
        private long _countCritical;

        private int _paused;
        private int _verbose;
        private int _disposed;

        public LogService(LogOptions options, IUiDispatcher? dispatcher = null, TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Normalize();
            _dispatcher = dispatcher;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _entriesView = new ReadOnlyObservableCollection<LogEntry>(_entries);
            _verbose = _options.Verbose ? 1 : 0;

            LogDirectory = _options.Directory;

            EnforceRetention(_options.RetentionDays);

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Zentwik.LogFile"
            };

            _writer.Start();
        }

        public ReadOnlyObservableCollection<LogEntry> Entries => _entriesView;

        public event Action<LogEntry>? EntryAdded;

        public bool Verbose
        {
            get => Volatile.Read(ref _verbose) == 1;
            set => Volatile.Write(ref _verbose, value ? 1 : 0);
        }

        public bool Paused
        {
            get => Volatile.Read(ref _paused) == 1;
            set => Volatile.Write(ref _paused, value ? 1 : 0);
        }

        public int Count
        {
            get
            {
                lock (_entriesGate)
                    return _entries.Count;
            }
        }

        public long CountInfo => Interlocked.Read(ref _countInfo);

        public long CountWarn => Interlocked.Read(ref _countWarn);

        public long CountError => Interlocked.Read(ref _countError);

        public long CountCritical => Interlocked.Read(ref _countCritical);

        public string LogDirectory { get; }

        public void Debug(string message)
        {
            if (Verbose)
                Write(LogLevel.Debug, message);
        }

        public void Info(string message) => Write(LogLevel.Info, message);

        public void Warn(string message) => Write(LogLevel.Warn, message);

        public void Error(string message) => Write(LogLevel.Error, message);

        public void Critical(string message)
        {
            Write(LogLevel.Critical, message);
            Flush();
        }

        public LogEntry[] Snapshot()
        {
            lock (_entriesGate)
                return [.. _entries];
        }

        public void Clear()
        {
            // 顺序不能反：先同步清队列并推进代号，再做内存清空与删文件。
            // 若把代号推进丢到 UI 线程异步执行，写盘线程会趁这个窗口把队列里的
            // 旧日志写进刚被删掉的文件 —— 用户点的是「清空」，看到的是旧日志原封不动回来了。
            lock (_fileGate)
            {
                _pendingFile.Clear();
                _generation++;
            }

            // 集合直接绑在界面列表上，从后台线程改会触发「不支持跨线程修改 SourceCollection」。
            if (_dispatcher is not null && !_dispatcher.IsCurrentThread)
                _dispatcher.Post(ClearMemory);
            else
                ClearMemory();

            PurgeLogFilesAsync();
        }

        public void Flush()
        {
            try
            {
                FlushPendingFile();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 退出阶段不能因为日志抛异常
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _stopSignal.Set();

            try
            {
                _writer.Join(TimeSpan.FromSeconds(2));
            }
            catch (ThreadStateException)
            {
                // 线程尚未启动就退出，无需等待
            }

            Flush();

            _fileSignal.Dispose();
            _stopSignal.Dispose();
        }

        private void ClearMemory()
        {
            bool locked = false;

            try
            {
                Monitor.TryEnter(_entriesGate, 100, ref locked);

                if (!locked)
                {
                    // 拿不到锁就放弃清列表，队列与文件照清 —— 至少下次不会被旧日志淹掉。
                    // 这里刻意不写日志：写日志本身要拿同一把锁，在一个「已经等不到锁」的
                    // 分支里再去排队，等于把 100ms 变成无限。
                    System.Diagnostics.Debug.WriteLine("清空日志：等待日志集合超时，已跳过界面清空");
                    return;
                }

                _entries.Clear();
                Interlocked.Exchange(ref _countInfo, 0);
                Interlocked.Exchange(ref _countWarn, 0);
                Interlocked.Exchange(ref _countError, 0);
                Interlocked.Exchange(ref _countCritical, 0);
            }
            finally
            {
                if (locked)
                    Monitor.Exit(_entriesGate);
            }
        }

        private void PurgeLogFilesAsync()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(LogDirectory))
                        return;

                    foreach (string file in Directory.EnumerateFiles(LogDirectory, LogPrefix + "*.log"))
                        DeleteWithRetry(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 清盘失败不影响清屏
                }
            });
        }

        private static void DeleteWithRetry(string file)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.IO.File.Delete(file);
                    return;
                }
                catch (IOException)
                {
                    // 句柄还开着，给写盘线程一点时间收尾
                    Thread.Sleep(30);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        private void EnforceRetention(int maxDays)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return;

                DateTime cutoff = DateTime.Today.AddDays(-maxDays);

                foreach (string file in EnumerateLogFiles())
                {
                    if (TryParseLogDate(file, out DateTime date) && date < cutoff)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 单个文件删不掉不影响其余留存清理
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 留存清理失败不影响启动
            }
        }

        private IEnumerable<string> EnumerateLogFiles()
        {
            foreach (string file in Directory.EnumerateFiles(LogDirectory, LogPrefix + "*.log"))
                yield return file;

            foreach (string prefix in LegacyLogPrefixes)
            {
                foreach (string file in Directory.EnumerateFiles(LogDirectory, prefix + "*.log"))
                    yield return file;
            }
        }

        private static bool TryParseLogDate(string file, out DateTime date)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int separator = name.IndexOf('-', StringComparison.Ordinal);

            if (separator < 0 || name.Length <= separator + 1)
            {
                date = default;
                return false;
            }

            return DateTime.TryParseExact(
                name[(separator + 1)..],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private void Write(LogLevel level, string message)
        {
            // 暂停期间一条都不记。Critical 例外 —— 程序要坏了，
            // 这时静默丢日志等于让用户永远查不出原因。
            if (Paused && level != LogLevel.Critical)
                return;

            LogEntry entry = new()
            {
                Time = _timeProvider.GetLocalNow().DateTime,
                Level = level,
                // Debug 不截断：它只落盘，磁盘是事后排查的唯一凭据，保留原文。
                Message = level == LogLevel.Debug ? message : Clip(message)
            };

            // Debug 只落盘：不进界面集合，也不往 UI 线程封送。
            if (level == LogLevel.Debug)
            {
                EnqueueFile(entry);
                return;
            }

            if (_dispatcher is not null && !_dispatcher.IsCurrentThread)
                _dispatcher.Post(() => AddEntry(entry));
            else
                AddEntry(entry);
        }

        private string Clip(string message)
        {
            if (message.Length <= _options.MaxMessageChars)
                return message;

            return string.Concat(
                message.AsSpan(0, _options.MaxMessageChars),
                "…(截断 ",
                (message.Length - _options.MaxMessageChars).ToString(CultureInfo.InvariantCulture),
                " 字符)");
        }

        private void AddEntry(LogEntry entry)
        {
            lock (_entriesGate)
            {
                _entries.Add(entry);
                BumpCount(entry.Level, +1);

                // 攒够一批再丢头部，别每条日志都让列表整体下移一次
                if (_entries.Count > _options.MaxEntries + _options.TrimBatch)
                {
                    for (int i = 0; i < _options.TrimBatch && _entries.Count > 0; i++)
                    {
                        // 丢弃时必须同步减计数，否则界面上的数字会和实际条数对不上
                        BumpCount(_entries[0].Level, -1);
                        _entries.RemoveAt(0);
                    }
                }
            }

            // 订阅者回调放到锁外：处理器可能做重操作，没必要占着集合锁
            EnqueueFile(entry);
            EntryAdded?.Invoke(entry);
        }

        private void BumpCount(LogLevel level, int delta)
        {
            switch (level)
            {
                case LogLevel.Info: Interlocked.Add(ref _countInfo, delta); break;
                case LogLevel.Warn: Interlocked.Add(ref _countWarn, delta); break;
                case LogLevel.Error: Interlocked.Add(ref _countError, delta); break;
                case LogLevel.Critical: Interlocked.Add(ref _countCritical, delta); break;
            }
        }

        private void EnqueueFile(LogEntry entry)
        {
            lock (_fileGate)
            {
                _pendingFile.Enqueue(entry);

                // 只丢最旧的一条、不整队列清空：清空会让「刚发生了什么」
                // 这段最关键的上下文一起消失。
                while (_pendingFile.Count > _options.MaxPendingEntries)
                    _pendingFile.Dequeue();
            }

            try
            {
                _fileSignal.Set();
            }
            catch (ObjectDisposedException)
            {
                // 退出过程中队列已释放，最后几条日志丢弃即可
            }
        }

        private void WriterLoop()
        {
            WaitHandle[] handles = [_fileSignal, _stopSignal];

            while (true)
            {
                try
                {
                    // 最多等 500ms：即使没有任何 Set，也要把这段时间攒下的条目写掉
                    int index = WaitHandle.WaitAny(handles, 500);

                    if (index == 1 || Volatile.Read(ref _disposed) == 1)
                    {
                        FlushPendingFile();
                        return;
                    }

                    FlushPendingFile();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // 写盘线程不能因为异常死掉
                }
            }
        }

        private void FlushPendingFile()
        {
            LogEntry[] batch;
            int generation;

            lock (_fileGate)
            {
                if (_pendingFile.Count == 0)
                    return;

                batch = [.. _pendingFile];
                _pendingFile.Clear();
                generation = _generation;
            }

            // 取队列时还没清空、写的时候已被清空：这批属于上一代，
            // 写出去就是把刚删掉的日志又搬回磁盘。
            if (Volatile.Read(ref _generation) != generation)
                return;

            try
            {
                // 目录只建一次：每条日志 CreateDirectory 是纯浪费
                if (!_directoryReady)
                {
                    Directory.CreateDirectory(LogDirectory);
                    _directoryReady = true;
                }

                // 按天分组，同一天的条目拼成一个字符串、只开一次文件
                foreach (IGrouping<string, LogEntry> group in batch.GroupBy(e => e.Time.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
                {
                    StringBuilder builder = new();

                    foreach (LogEntry entry in group)
                    {
                        builder.Append('[').Append(entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] [")
                               .Append(entry.LevelText).Append("] ")
                               .Append(entry.Message).Append(Environment.NewLine);
                    }

                    // FileShare.ReadWrite | FileShare.Delete：允许清空日志的后台任务与
                    // 资源管理器在我们写着的时候读、删。AppendAllText 默认只给 Read，
                    // 一旦用户点「清空日志」，那个文件就再也删不掉了。
                    using FileStream stream = new(
                        Path.Combine(LogDirectory, $"{LogPrefix}{group.Key}.log"),
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);

                    using StreamWriter writer = new(stream, new UTF8Encoding(false));
                    writer.Write(builder.ToString());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写盘失败绝不能影响主程序
            }
        }
    }
}
