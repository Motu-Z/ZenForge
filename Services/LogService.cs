using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ZenForge.Services
{
    public enum LogLevel
    {
        /// <summary>详细运行信息，只在诊断版 / 显式开启时才落盘。</summary>
        Debug,
        Info,
        Warn,
        Error
    }

    public sealed class LogEntry
    {
        public DateTime Time { get; init; }
        public LogLevel Level { get; init; }
        public string Message { get; init; } = string.Empty;

        public string LevelText => Level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };

        /// <summary>Debug 条目在界面日志里默认隐藏，避免刷屏。</summary>
        public bool IsDebug => Level == LogLevel.Debug;
    }

    /// <summary>
    /// 极简日志服务：内存集合（供 UI 绑定）+ 按天滚动的日志文件。
    /// S2：后台线程（SMU 探测）写日志时，通过 <see cref="UiContext"/> 自动封送回 UI 线程，
    /// 避免 ObservableCollection 跨线程修改导致绑定崩溃。
    /// </summary>
    public static class LogService
    {
        private static readonly ObservableCollection<LogEntry> EntriesInternal = new();

        /// <summary>
        /// 保护 <see cref="EntriesInternal"/> 的并发写入。
        ///
        /// UI 模式下所有写都由 <see cref="UiContext"/> 封送到 UI 线程，天然串行；
        /// 但纯后台模式（-silent，没有 UiContext）下，WMI 回调线程与降级轮询线程
        /// 会并发写同一个 ObservableCollection —— 不加锁就是随机的
        /// InvalidOperationException（"Collection was modified"）。
        /// </summary>
        private static readonly object EntriesGate = new();

        /// <summary>
        /// 界面日志条数上限。
        ///
        /// 程序现在是常驻的（关窗收进托盘后一直活着），日志集合只增不减会把内存
        /// 一点点吃光。超限后从头部丢弃，保证内存有界。
        /// </summary>
        private const int MaxEntries = 2000;

        /// <summary>
        /// 单次丢弃的批量。
        ///
        /// 每删一条，界面上的列表就要整体下移一次。逐条删等于每条日志都让
        /// UI 重排整个列表，攒够一批再删就只有批量那一刻有开销。
        /// </summary>
        private const int TrimBatch = 256;

        /// <summary>日志文件最长留存天数，超过的启动即删。</summary>
        private const int MaxRetentionDays = 14;

        // ==================== 落盘：后台批量写 ====================
        //
        // 以前每条日志都在 UI 线程上 CreateDirectory + File.AppendAllText。
        // 绑一次核就是几十个进程、几十条日志，等于在 UI 线程上连打几十次磁盘，
        // 界面会明显顿一下。现在只把条目排进队列，由后台线程每 500ms 成批写出。

        private static readonly object FileGate = new();
        private static readonly Queue<LogEntry> PendingFile = new();
        private static bool _directoryReady;
        private static readonly ManualResetEventSlim FileSignal = new(false);

        static LogService()
        {
            // 启动时清理超过 14 天的日志文件：程序常驻后台，日志一天天累积，
            // 没有留存策略就会一直堆在 %APPDATA% 里。
            EnforceRetention(MaxRetentionDays);

            Thread writer = new(FileLoop)
            {
                IsBackground = true,
                Name = "ZenForge.LogFile"
            };
            writer.Start();
        }

        /// <summary>UI 线程的同步上下文，由 App.xaml.cs 在启动时注入。</summary>
        public static SynchronizationContext? UiContext { get; set; }

        public static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZenForge", "Logs");

        public static ObservableCollection<LogEntry> Entries => EntriesInternal;

        public static event Action<LogEntry>? EntryAdded;

        /// <summary>
        /// 是否记录 Debug 级详细日志。
        ///
        /// 开启方式（任一即可）：
        ///   · 诊断版 exe（编译时定义了 VERBOSE_LOG，默认开启）；
        ///   · 环境变量 ZENFORGE_DEBUG=1；
        ///   · 命令行参数 -debug / --debug。
        /// 默认关闭，因为它会把每次焦点变化、每次列表刷新都写进去，
        /// 长期开着会让日志文件快速膨胀。
        /// </summary>
        public static bool Verbose { get; set; } =
#if VERBOSE_LOG
            true;
#else
            false;
#endif

        /// <summary>详细日志：仅当 <see cref="Verbose"/> 为真时才写入。</summary>
        public static void Debug(string message)
        {
            if (Verbose)
                Write(LogLevel.Debug, message);
        }

        public static void Info(string message) => Write(LogLevel.Info, message);

        public static void Warn(string message) => Write(LogLevel.Warn, message);

        public static void Error(string message) => Write(LogLevel.Error, message);

        public static void Clear()
        {
            // EntriesInternal 直接绑在 UI 的 ListBox 上。若从后台线程调用 Clear()，
            // 会在后台线程改 ObservableCollection，触发
            // 「该类型的 CollectionView 不支持从不同于 Dispatcher 的线程更改 SourceCollection」。
            // 和 Write 一样，统一切到 UI 线程执行。
            if (UiContext != null && SynchronizationContext.Current != UiContext)
            {
                UiContext.Post(_ => Clear(), null);
                return;
            }

            lock (EntriesGate)
            {
                EntriesInternal.Clear();
            }

            // 把还没落盘的队列也清掉：否则这些条目会被重新写进刚清空的日志文件，
            // 「清空」反而变成「把旧日志搬到新文件」。
            lock (FileGate)
                PendingFile.Clear();

            // 连磁盘文件一起清：清空日志就该彻底，界面与文件一致。
            PurgeLogFiles();
        }

        /// <summary>删除磁盘上全部日志文件（清空日志时调用）。</summary>
        private static void PurgeLogFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return;

                foreach (string file in Directory.EnumerateFiles(LogDirectory, "zenforge-*.log"))
                {
                    try { File.Delete(file); }
                    catch { /* 文件正被写入时删不掉，忽略，下次启动再清 */ }
                }
            }
            catch
            {
                // 清盘失败不应影响清屏
            }
        }

        /// <summary>启动时删除超过 <paramref name="maxDays"/> 天的日志文件。</summary>
        private static void EnforceRetention(int maxDays)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return;

                DateTime cutoff = DateTime.Today.AddDays(-maxDays);
                const string Prefix = "zenforge-";

                foreach (string file in Directory.EnumerateFiles(LogDirectory, Prefix + "*.log"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length <= Prefix.Length)
                        continue;

                    // 文件名含日期：zenforge-YYYYMMDD.log
                    if (DateTime.TryParseExact(
                            name.Substring(Prefix.Length),
                            "yyyyMMdd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateTime date)
                        && date < cutoff)
                    {
                        try { File.Delete(file); }
                        catch { /* 忽略单个文件 */ }
                    }
                }
            }
            catch
            {
                // 留存清理失败不影响启动
            }
        }

        private static void Write(LogLevel level, string message)
        {
            LogEntry entry = new()
            {
                Time = DateTime.Now,
                Level = level,
                Message = message
            };

            // Debug 只落盘：不进 UI 集合，也不往 UI 线程封送。
            // 详细日志动辄每秒几十条，塞进界面日志会直接刷屏，
            // 而它的用途本来也只是事后分析。
            if (level == LogLevel.Debug)
            {
                EnqueueFile(entry);
                return;
            }

            if (UiContext != null && SynchronizationContext.Current != UiContext)
                UiContext.Post(_ => WriteOnUiThread(entry), null);
            else
                WriteOnUiThread(entry);
        }

        private static void WriteOnUiThread(LogEntry entry)
        {
            // UI 模式下这一处永远在 UI 线程上，锁是可重入的，开销可忽略；
            // 纯后台模式下它是唯一的并发保护，不能省。
            lock (EntriesGate)
            {
                EntriesInternal.Add(entry);

                // 攒够一批再丢头部，别每条日志都让列表整体下移一次
                if (EntriesInternal.Count > MaxEntries + TrimBatch)
                {
                    for (int i = 0; i < TrimBatch && EntriesInternal.Count > 0; i++)
                        EntriesInternal.RemoveAt(0);
                }
            }

            // 订阅者回调放到锁外：UI 处理器可能做重操作，没必要占着集合锁
            EnqueueFile(entry);
            EntryAdded?.Invoke(entry);
        }

        /// <summary>把条目排进落盘队列。UI 线程上只做入队，真正的写由后台线程完成。</summary>
        private static void EnqueueFile(LogEntry entry)
        {
            lock (FileGate)
                PendingFile.Enqueue(entry);

            FileSignal.Set();
        }

        private static void FileLoop()
        {
            while (true)
            {
                try
                {
                    FileSignal.Wait(500);
                    FileSignal.Reset();
                    FlushPendingFile();
                }
                catch
                {
                    // 写盘线程不能因为任何异常死掉
                }
            }
        }

        /// <summary>把队列里攒下的条目一次写完。退出前显式调用一次，避免丢最后几条。</summary>
        public static void Flush()
        {
            try
            {
                FlushPendingFile();
            }
            catch
            {
                // 退出阶段不能因为日志抛异常
            }
        }

        private static void FlushPendingFile()
        {
            LogEntry[] batch;
            lock (FileGate)
            {
                if (PendingFile.Count == 0)
                    return;

                batch = PendingFile.ToArray();
                PendingFile.Clear();
            }

            try
            {
                // 目录只建一次：每条日志 CreateDirectory 是纯浪费
                if (!_directoryReady)
                {
                    Directory.CreateDirectory(LogDirectory);
                    _directoryReady = true;
                }

                // 按天分组，同一天的条目拼成一个字符串、只开一次文件
                foreach (IGrouping<string, LogEntry> group in batch.GroupBy(e => e.Time.ToString("yyyyMMdd")))
                {
                    StringBuilder sb = new();
                    foreach (LogEntry entry in group)
                    {
                        sb.Append('[').Append(entry.Time.ToString("HH:mm:ss")).Append("] [")
                          .Append(entry.LevelText).Append("] ")
                          .Append(entry.Message).Append(Environment.NewLine);
                    }

                    File.AppendAllText(Path.Combine(LogDirectory, $"zenforge-{group.Key}.log"), sb.ToString());
                }
            }
            catch
            {
                // 日志写盘失败绝不能影响主程序
            }
        }
    }
}
