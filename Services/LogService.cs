using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZenForge.Services
{
    public enum LogLevel
    {
        /// <summary>详细运行信息，只在诊断版 / 显式开启时才落盘。</summary>
        Debug,
        Info,
        Warn,
        Error,

        /// <summary>
        /// 致命错误：进程已无法继续正常工作（异常兜底、关键服务启动失败）。
        ///
        /// 与 Error 的区别不是「更严重一点」，而是**要不要用户动手**：
        /// Error 是「这次操作失败了，重试或换个参数」；
        /// Critical 是「程序坏了，请把日志发我」。
        /// 单独成级是为了让它有独立的固定色（Log.Critical 深红），
        /// 在日志列表里一眼就能从一片 Error 里挑出来。
        /// </summary>
        Critical
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
            LogLevel.Critical => "FATAL",
            _ => "INFO"
        };
    }

    /// <summary>
    /// 极简日志服务：内存集合（供 UI 绑定）+ 按天滚动的日志文件。
    /// 后台线程（SMU 探测）写日志时，通过 <see cref="UiContext"/> 自动封送回 UI 线程，
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

        private const string LogPrefix = "zenforge-";

        // ==================== 落盘：后台批量写 ====================
        //
        // 以前每条日志都在 UI 线程上 CreateDirectory + File.AppendAllText。
        // 绑一次核就是几十个进程、几十条日志，等于在 UI 线程上连打几十次磁盘，
        // 界面会明显顿一下。现在只把条目排进队列，由后台线程每 500ms 成批写出。

        private static readonly object FileGate = new();
        private static readonly Queue<LogEntry> PendingFile = new();
        private static bool _directoryReady;

        /// <summary>
        /// 日志「代号」。每次清空 +1，写盘线程在真正落盘前核对一次：
        /// 对不上说明这批已经被用户清掉了，直接丢弃，别把旧日志又写回刚删掉的文件。
        /// </summary>
        private static int _generation;
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

        /// <summary>
        /// 致命错误：进程即将或已经无法继续。异常兜底走这条。
        /// 同时会立刻冲一次盘 —— 走到这里时进程可能下一行就没了。
        /// </summary>
        public static void Critical(string message)
        {
            Write(LogLevel.Critical, message);
            try { Flush(); } catch { }
        }

        /// <summary>
        /// 清空日志：内存集合 + 待写队列 + 磁盘文件。
        ///
        /// **UI 线程上只做不碰磁盘的那一半。** 以前这里在 UI 线程直接
        /// `Directory.EnumerateFiles` + `File.Delete`，而写盘线程可能正拿着
        /// 同一个文件的句柄（`File.AppendAllText` 默认只允许 Read 共享）——
        /// 删不掉是小事，卡住界面才是大事。现在删除整个丢给后台线程。
        /// </summary>
        public static void Clear()
        {
            // EntriesInternal 直接绑在 UI 的 ListBox 上。若从后台线程调用，
            // 会在后台线程改 ObservableCollection，触发
            // 「该类型的 CollectionView 不支持从不同于 Dispatcher 的线程更改 SourceCollection」。
            // 和 Write 一样，统一切到 UI 线程执行。
            if (UiContext != null && SynchronizationContext.Current != UiContext)
                UiContext.Post(_ => ClearMemory(), null);
            else
                ClearMemory();

            PurgeLogFilesAsync();
        }

        /// <summary>
        /// 只在内存里清：集合与落盘队列。全部是 O(n) 的数组清空，不碰 IO、不碰文件系统。
        ///
        /// 用 TryEnter 而不是 lock：万一有哪条路径把 EntriesGate 抓在手里不放，
        /// 这里宁可放弃这次清空并记一笔，也不能让 UI 线程陪着一起等死。
        /// </summary>
        private static void ClearMemory()
        {
            bool locked = false;
            try
            {
                Monitor.TryEnter(EntriesGate, 100, ref locked);
                if (!locked)
                {
                    // 拿不到锁就放弃清列表，队列与文件照清 —— 至少下次不会被旧日志淹掉。
                    // 这里刻意**不写 LogService**：写日志本身要拿同一把锁，
                    // 在一个「已经等不到锁」的分支里再去排队，等于把 100ms 变成无限。
                    System.Diagnostics.Debug.WriteLine("清空日志：等待日志集合超时，已跳过界面清空");
                }
                else
                {
                    EntriesInternal.Clear();
                }
            }
            finally
            {
                if (locked)
                    Monitor.Exit(EntriesGate);
            }

            // 把还没落盘的队列也清掉：否则这些条目会被重新写进刚清空的日志文件，
            // 「清空」反而变成「把旧日志搬到新文件」。
            // 同时推进代号，让已经取走但还没写的那一批自己作废。
            lock (FileGate)
            {
                PendingFile.Clear();
                _generation++;
            }
        }

        /// <summary>
        /// 后台删除磁盘上的全部日志文件。
        ///
        /// 删不掉通常是写盘线程正持有句柄（Windows 的共享模式），
        /// 短暂重试几次；仍失败就留给下次启动时的留存清理，不为它阻塞任何人。
        /// </summary>
        private static void PurgeLogFilesAsync()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(LogDirectory))
                        return;

                    foreach (string file in Directory.EnumerateFiles(LogDirectory, "zenforge-*.log"))
                        DeleteWithRetry(file);
                }
                catch
                {
                    // 清盘失败不应影响清屏
                }
            });
        }

        /// <summary>删不掉通常是写盘线程正持有句柄（Windows 的共享模式），短暂重试几次。</summary>
        private static void DeleteWithRetry(string file)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Delete(file);
                    return;
                }
                catch (IOException)
                {
                    // 句柄还开着，给写盘线程一点时间收尾
                    Thread.Sleep(30);
                }
                catch
                {
                    return;
                }
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

                foreach (string file in Directory.EnumerateFiles(LogDirectory, LogPrefix + "*.log"))
                {
                    // 文件名含日期：zenforge-YYYYMMDD.log
                    if (TryParseLogDate(file, out DateTime date) && date < cutoff)
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

        private static bool TryParseLogDate(string file, out DateTime date)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length <= LogPrefix.Length)
            {
                date = default;
                return false;
            }

            return DateTime.TryParseExact(name.Substring(LogPrefix.Length), "yyyyMMdd",
                                          CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
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
            int generation;
            lock (FileGate)
            {
                if (PendingFile.Count == 0)
                    return;

                batch = PendingFile.ToArray();
                PendingFile.Clear();
                generation = _generation;
            }

            // 取队列的时候还没清空、写的时候已经被清了：这批属于「上一代」，
            // 写出去就是把刚删掉的日志又搬回磁盘。直接丢弃。
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
                foreach (IGrouping<string, LogEntry> group in batch.GroupBy(e => e.Time.ToString("yyyyMMdd")))
                {
                    StringBuilder sb = new();
                    foreach (LogEntry entry in group)
                    {
                        sb.Append('[').Append(entry.Time.ToString("HH:mm:ss")).Append("] [")
                          .Append(entry.LevelText).Append("] ")
                          .Append(entry.Message).Append(Environment.NewLine);
                    }

                    // FileShare.ReadWrite | FileShare.Delete：允许别人（清空日志的后台任务、
                    // 资源管理器）在我们写着的时候读、删。AppendAllText 默认只给 Read，
                    // 一旦用户点「清空日志」，那个文件就再也删不掉了。
                    using FileStream fs = new(
                        Path.Combine(LogDirectory, $"zenforge-{group.Key}.log"),
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    using StreamWriter writer = new(fs, new UTF8Encoding(false));
                    writer.Write(sb.ToString());
                }
            }
            catch
            {
                // 日志写盘失败绝不能影响主程序
            }
        }
    }
}
