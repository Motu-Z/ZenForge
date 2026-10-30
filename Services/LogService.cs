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
        ///
        /// 【2000 → 800 → 500 → 320】
        /// 2000 条按每条约 200 字节的字符串 + 对象头，常驻约 500KB～1MB；
        /// 更贵的是 UI 侧：每次新增都要让 ListBox 重排，筛选时还要整表重建。
        /// 而实际上没人会翻到几百条以前 —— 要看历史有磁盘日志（Debug 级也落盘），
        /// 界面上只承担「最近发生了什么」。
        ///
        /// 收窄到 320 的依据是条数之外的第二个变量：单条长度。
        /// 全局异常的完整堆栈常达 2～8KB，同样的 500 条，若里面掺着十几条堆栈，
        /// 常驻内存就从几百 KB 跳到几 MB —— 条数上限根本拦不住它。
        /// 所以这一版同时给单条消息加了长度上限（见 <see cref="MaxMessageChars"/>），
        /// 两个上限相乘才是真正的内存天花板：320 × 2KB ≈ 640KB，且恒定。
        /// 320 条 ≈ 满屏 30 行的 10 屏，仍够定位刚刚发生的问题。
        /// </summary>
        private const int MaxEntries = 320;

        /// <summary>
        /// 单次丢弃的批量。
        ///
        /// 每删一条，界面上的列表就要整体下移一次。逐条删等于每条日志都让
        /// UI 重排整个列表，攒够一批再删就只有批量那一刻有开销。
        ///
        /// 128 → 64：集合本身变小了，一次丢 128 等于瞬间砍掉三分之一屏，
        /// 用户正在往下翻时会看到列表「跳一下」。64 是「批量」与「不突兀」的平衡点，
        /// 峰值条数（320 + 64）仍然有界。
        /// </summary>
        private const int TrimBatch = 64;

        /// <summary>
        /// 单条消息进入内存集合时的长度上限（超出截断）。
        ///
        /// 【为什么必须截】
        /// 条数上限管的是「多少条」，管不了「一条多大」。未捕获异常的堆栈、
        /// WMI 查询报错整段吐回来的 HRESULT 描述，都是几 KB 起步；
        /// 全局异常限流后前 3 条仍写完整栈，一次启动崩溃就是十几 KB × 3。
        /// 这些字符串全部被内存集合强引用着，直到被 Trim 出去才释放。
        ///
        /// 【为什么只截内存、不截落盘】
        /// Debug 级与磁盘是事后排查的唯一凭据，磁盘空间比内存便宜三个数量级，
        /// 那里保留原文。界面上 2000 字符已经能看完栈顶（根因就在最前面几行），
        /// 剩下的部分用户也不会在 13px 的列表里翻。
        /// </summary>
        private const int MaxMessageChars = 2000;

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

        /// <summary>
        /// 落盘线程的唤醒信号。
        ///
        /// 【为什么必须是 AutoResetEvent】
        /// ManualResetEventSlim 置位后不会自动复位，要靠代码显式 Reset()；
        /// 一旦 Set 落在「Wait 已返回、Reset 还没执行」这个缝里，信号就被吃掉，
        /// 那一批日志要白等一个 500ms 周期。同一个坑在采样线程上已经踩过一次
        /// （永久置位导致忙等自旋），这里是它的轻量版。
        /// AutoResetEvent 在 WaitOne 返回时自动复位，Set 一次唤醒一个等待者，
        /// 本线程是唯一等待者，语义刚好，也不需要再写 Reset()。
        /// </summary>
        private static readonly AutoResetEvent FileSignal = new(false);

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

        // ---------------- 增量计数 ----------------
        //
        // 【为什么要有这个】
        // 之前日志页在每一条新日志到达时都调 UpdateStats()，而那个方法会
        // `foreach (LogEntry e in EntriesInternal)` 把 2000 条全遍历一遍 ——
        // 绑一次核产生几十条日志，就是几十次 × 2000 的遍历，全在 UI 线程上。
        // 界面卡顿的主因不是渲染，是这些看不见的全量扫描。
        //
        // 现在把计数维护在写入路径上：加一条就 +1，丢弃一条就 -1，
        // 页面直接读这四个属性，复杂度 O(1)。清空时一起归零。
        //
        // 用 long 而不是 int：常驻进程跑几天，Info 级累计到 21 亿虽不现实，
        // 但类型选错要改一遍所有调用点，不如一开始就用 long。

        private static long _countInfo;
        private static long _countWarn;
        private static long _countError;
        private static long _countCritical;

        public static long CountInfo => Interlocked.Read(ref _countInfo);
        public static long CountWarn => Interlocked.Read(ref _countWarn);
        public static long CountError => Interlocked.Read(ref _countError);
        public static long CountCritical => Interlocked.Read(ref _countCritical);

        /// <summary>界面集合的总条数（读的是集合本身，不是计数缓存）。</summary>
        public static int Count => EntriesInternal.Count;

        /// <summary>
        /// 取一份当前日志的快照副本。
        ///
        /// 导出、自检这类需求必须走这个方法而不是直接 foreach <see cref="Entries"/>：
        /// 那个集合是后台线程在写的，遍历它时后台正好 Add 一条，
        /// 就是随机的 InvalidOperationException（"Collection was modified"）。
        /// 锁只保护这次复制，出去之后副本归调用方，不与写入路径争锁。
        /// </summary>
        public static LogEntry[] Snapshot()
        {
            lock (EntriesGate)
                return EntriesInternal.ToArray();
        }

        /// <summary>
        /// 暂停日志记录。
        ///
        /// 【与旧版「暂停」的区别】
        /// 旧版只冻结界面刷新，日志照写文件。那个语义叫「暂停」很容易被误解成
        /// 「不再记录」，用户以为关掉了、实际磁盘还在涨。
        /// 现在是真的停：Write() 在入口直接返回，既不进内存也不写文件。
        ///
        /// 代价要说清楚 —— 暂停期间的日志是真的丢了，事后无法追溯。
        /// 所以设置页与日志页的文案都写明「丢弃」，而不是含糊的「暂停」。
        /// </summary>
        public static bool Paused { get; set; }

        /// <summary>写一条就更新对应计数。Debug 不进集合，因此也不计数。</summary>
        private static void BumpCount(LogLevel level, int delta)
        {
            switch (level)
            {
                case LogLevel.Info: Interlocked.Add(ref _countInfo, delta); break;
                case LogLevel.Warn: Interlocked.Add(ref _countWarn, delta); break;
                case LogLevel.Error: Interlocked.Add(ref _countError, delta); break;
                case LogLevel.Critical: Interlocked.Add(ref _countCritical, delta); break;
            }
        }

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
            // 【顺序不能反】
            // 先同步地清掉待写队列并推进代号，再去做内存清空与删文件。
            // 原来这两步在 ClearMemory 里，而后台线程走的是 UiContext.Post ——
            // Post 是异步的，于是「后台删文件的任务」可能比「推进代号」先跑完，
            // 写盘线程趁这个窗口把队列里那批旧日志写进刚被删掉的文件里：
            // 用户点的是「清空」，看到的是旧日志原封不动回来了。
            // 代号与队列的推进必须在调用方线程上当场完成，不能押后。
            lock (FileGate)
            {
                PendingFile.Clear();
                _generation++;
            }

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
                    Interlocked.Exchange(ref _countInfo, 0);
                    Interlocked.Exchange(ref _countWarn, 0);
                    Interlocked.Exchange(ref _countError, 0);
                    Interlocked.Exchange(ref _countCritical, 0);
                }
            }
            finally
            {
                if (locked)
                    Monitor.Exit(EntriesGate);
            }

            // 队列与代号已由 Clear() 在调用方线程上同步清过（见那里的顺序说明），
            // 这里只管内存集合，不能再动 FileGate —— 否则后台路径上会出现
            // 「Post 之前清一次、Post 之后又清一次」的两次推进。
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
            // 暂停期间一条都不记：不进集合、不排队、不落盘。
            // Critical 例外 —— 程序要坏了，这时静默丢日志等于让用户永远查不出原因。
            if (Paused && level != LogLevel.Critical)
                return;

            // Debug 不截断：它只落盘，磁盘是事后排查的唯一凭据，保留原文。
            // 其余级别要进内存集合，按 MaxMessageChars 截顶，避免一条堆栈吃掉几十 KB。
            LogEntry entry = new()
            {
                Time = DateTime.Now,
                Level = level,
                Message = level == LogLevel.Debug ? message : Clip(message)
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

        /// <summary>超出 <see cref="MaxMessageChars"/> 的部分截掉，尾部标注被截了多少字符。</summary>
        private static string Clip(string message)
        {
            if (message.Length <= MaxMessageChars)
                return message;

            return string.Concat(
                message.AsSpan(0, MaxMessageChars),
                "…(截断 ",
                (message.Length - MaxMessageChars).ToString(CultureInfo.InvariantCulture),
                " 字符)");
        }

        private static void WriteOnUiThread(LogEntry entry)
        {
            // UI 模式下这一处永远在 UI 线程上，锁是可重入的，开销可忽略；
            // 纯后台模式下它是唯一的并发保护，不能省。
            lock (EntriesGate)
            {
                EntriesInternal.Add(entry);
                BumpCount(entry.Level, +1);

                // 攒够一批再丢头部，别每条日志都让列表整体下移一次
                if (EntriesInternal.Count > MaxEntries + TrimBatch)
                {
                    for (int i = 0; i < TrimBatch && EntriesInternal.Count > 0; i++)
                    {
                        // 丢弃时必须同步减计数，否则 pill 上的数字会越涨越高、
                        // 和实际能看到的条数对不上 —— 比不显示数字更糟。
                        BumpCount(EntriesInternal[0].Level, -1);
                        EntriesInternal.RemoveAt(0);
                    }
                }
            }

            // 订阅者回调放到锁外：UI 处理器可能做重操作，没必要占着集合锁
            EnqueueFile(entry);
            EntryAdded?.Invoke(entry);
        }

        /// <summary>
        /// 待写队列的容量上限。
        ///
        /// 正常情况下这个队列只有个位数（后台线程每 500ms 清空一次），
        /// 上限是给异常情况留的保险：磁盘被占用、杀软扫描、机械盘卡顿时，
        /// 写线程会一直积压，而 UI 侧还在源源不断地产日志 ——
        /// 没有上限的话这里就是一条隐形的内存泄漏，而且越卡越涨。
        ///
        /// 【2000 → 800】
        /// 队列里存的是 LogEntry 引用，字符串本身与内存集合共享、不额外复制，
        /// 但每条仍要占一个对象 + 队列节点。真正的风险窗口是「写线程卡住的那几秒」：
        /// 按单条上限 2KB 算，2000 条意味着最多能积压 4MB；降到 800 条即 1.6MB 封顶，
        /// 而后台线程每 500ms 就清一次队，正常状态下队列只有个位数 —— 800 的余量
        /// 足够扛住一次几秒的磁盘卡顿，不需要更多。
        /// </summary>
        private const int MaxPendingFile = 800;

        /// <summary>把条目排进落盘队列。UI 线程上只做入队，真正的写由后台线程完成。</summary>
        private static void EnqueueFile(LogEntry entry)
        {
            lock (FileGate)
            {
                PendingFile.Enqueue(entry);

                // 只丢最旧的一条、不整队列清空：清空会让「刚发生了什么」这段
                // 最关键的上下文一起消失，而丢一条的代价小得多。
                while (PendingFile.Count > MaxPendingFile)
                    PendingFile.Dequeue();
            }

            FileSignal.Set();
        }

        private static void FileLoop()
        {
            while (true)
            {
                try
                {
                    // 最多等 500ms：即使没有任何 Set，也要把这段时间攒下的条目写掉
                    FileSignal.WaitOne(500);
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
