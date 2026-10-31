using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 进程快照服务（低占用版）。
    ///
    /// 关键点：
    ///   1. 用 Win32 <c>Toolhelp32Snapshot</c> 一次性拿全系统进程名 + PID，
    ///      不再逐个 <c>Process.GetProcesses()</c> 再打开 <c>MainModule</c>（那会为每个进程
    ///      打开模块句柄，权限不足时还抛异常，开销与失败率都很高）。
    ///   2. 可执行文件路径用 <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> +
    ///      <c>QueryFullProcessImageName</c> 取，权限要求比 MainModule 低得多；
    ///      并按 PID 缓存。<b>解析失败会退避重试</b>，见 <see cref="ResolvePath"/>。
    ///   3. CPU 占用率用 <c>GetProcessTimes</c> 做<b>滑动窗口平均</b>，见 <see cref="ComputeCpu"/>。
    ///
    /// 采样跑在后台线程上（旧实现用 DispatcherTimer，整轮 Toolhelp 遍历与对每个进程的
    /// OpenProcess 都压在 UI 线程，是列表掉帧的直接原因）。
    ///
    /// 生命周期：应用启动即开始周期采样，调度页打开后直接消费最新快照。
    /// </summary>
    public static class ProcessRefresher
    {
        // ---------- Win32 互操作 ----------

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // 必须是 64 位全 1：new IntPtr(uint) 会零扩展成 0x00000000FFFFFFFF，
        // 与 64 位进程里真实的 INVALID_HANDLE_VALUE（0xFFFFFFFFFFFFFFFF）比不上。
        private const long INVALID_HANDLE_VALUE = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(
            IntPtr hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        // ---------- 采样状态 ----------

        /// <summary>单个进程的采样记录（用于 CPU 滑动窗口与路径缓存）。</summary>
        private sealed class ProcSample
        {
            public long KernelTicks;
            public long UserTicks;
            public long StampTicks;

            public string Path = string.Empty;
            public bool PathResolved;

            /// <summary>上一次取路径失败的时间戳（0 = 从未失败过）。用于退避重试。</summary>
            public long PathFailStamp;

            /// <summary>CPU 占用滑动窗口：(采样时刻, 当时的累计 CPU 时间)。</summary>
            public readonly Queue<(long Stamp, long Ticks)> CpuWindow = new();
        }

        /// <summary>一个进程的快照条目。</summary>
        public sealed class Entry
        {
            public string Name { get; init; } = string.Empty;
            public int Pid { get; init; }
            public string Path { get; init; } = string.Empty;
            public double Cpu { get; init; }

            /// <summary>
            /// 进程基础优先级类（来自 PROCESSENTRY32.pcPriClassBase），只读展示用。
            ///
            /// 只取不设：SetPriorityClass 提到 RealTime / High 可能让一个忙循环的进程
            /// 独占 CPU，把输入响应一起锁死（整机看起来像卡死）。而 pcPriClassBase
            /// 是 Toolhelp 快照自带字段，不需要 OpenProcess 额外权限，
            /// 对受保护进程也能拿到值，成本为零。
            /// </summary>
            public int PriorityClass { get; init; }
        }

        private static readonly Dictionary<int, ProcSample> _samples = new();
        private static readonly List<Entry> _latest = new();
        private static readonly object _gate = new();

        /// <summary>
        /// 保护 <see cref="_samples"/>：单例字典会被采样线程与任何外部 Sample 调用同时接触。
        /// </summary>
        private static readonly object _sampleGate = new();

        private static CancellationTokenSource? _cts;
        private static int _loopRunning;

        /// <summary>
        /// 锁住 <see cref="_watcherCount"/> 的增减与唤醒信号。
        /// </summary>
        private static readonly object _wakeGate = new();

        /// <summary>
        /// 「有人在看」的唤醒事件。计数从 0 变 1 时置位，采样线程随即醒来。
        ///
        /// 【为什么改成「专用 STA 线程 + AutoResetEvent」而不是 Task.Run 循环】
        /// 旧实现用 Task.Run 跑异步循环，采样过程在线程池线程（MTA）上完成。
        /// 纯 Win32 调用不受影响，但只要有任何一个订阅方在回调里碰到了 WPF 对象
        /// （哪怕只是读一个 DependencyProperty），立刻抛
        /// 「调用线程必须为 STA，因为许多 UI 组件都需要」。
        /// 这个异常发生在【采样线程】内部，被循环体的 catch 吞成一条 WARN，
        /// 于是表现成「每隔几秒一条 WARN + 列表永远不更新」，而堆栈里看不到任何 UI 代码。
        ///
        /// 现在：一条命名后台线程，显式设成 STA，采样与回调都在它上面串行发生。
        /// 【但订阅方仍必须自己 Dispatcher.BeginInvoke 回 UI 线程】——
        /// STA 只解决「单元状态」，不解决「这不是 UI 线程」。
        /// 见 ProcessBindingPage.OnProcessesRefreshed。
        ///
        /// 【必须是 AutoResetEventSlim，不能是 ManualResetEventSlim】
        /// 1.0.2 的实测教训：这里原本是 ManualResetEventSlim，只 Set 从不 Reset，
        /// 于是「第一次打开进程页」把信号永久置位。之后无论有没有人看，
        /// 采样线程在「没人看」分支里的 WaitAny 都会**立刻返回**，变成：
        ///
        ///     WaitAny → 立即返回 → continue → WaitAny → 立即返回 → ……
        ///
        /// 一个占满核心的忙等自旋。任务管理器上表现为「收进托盘后仍有约 3% CPU」
        /// （16 核 × BelowNormal 优先级 ≈ 0.5 核 ≈ 3.1%）。
        /// 切走页面也停不下来 —— 停掉的是引用计数，信号还一直置着。
        ///
        /// AutoResetEvent 的语义正好：一次 Set 只唤醒一个等待者，
        /// 唤醒后自动复位，等待方再次 Wait 就会真的睡下去。
        /// （.NET 没有 AutoResetEventSlim，只有 ManualResetEventSlim。）
        /// </summary>
        private static readonly AutoResetEvent _watchSignal = new(false);

        /// <summary>「立即采一帧」请求。与观察者信号分开，避免互相吃掉。</summary>
        private static readonly SemaphoreSlim _sampleNowSignal = new(0);

        private static Thread? _samplerThread;

        /// <summary>
        /// 「正在看进程列表」的引用计数。
        ///
        /// 0.7.0 起自动绑核已改由 ProcessWatcher 的 WMI 事件驱动，这份快照只服务于
        /// 「用户正在看列表」这一个用途。既然没人看，就没有任何理由继续采样 ——
        /// 所以这里不是「降频到某个慢周期」，而是完全停下。
        /// 用计数而不是布尔值，是为了容纳「窗口可见 + 页面已加载」这类多个条件
        /// 各自持有、各自释放的场景，避免其中一方先释放就把采集误停。
        /// </summary>
        private static int _watcherCount;

        /// <summary>
        /// 宿主窗口是否可见（volatile：UI 线程写、采样线程读）。
        /// false 时采样循环彻底挂起，不看有没有人「在看列表」。
        /// </summary>
        private static volatile bool _hostVisible = true;

        /// <summary>当前是否有人在看（有任何一个观察者持有）。</summary>
        public static bool IsWatched
        {
            get { lock (_wakeGate) return _watcherCount > 0; }
        }

        /// <summary>
        /// 登记一个观察者，开始按 <see cref="IntervalSeconds"/> 采集。
        /// 与 <see cref="ReleaseWatch"/> 必须成对调用（页面 Loaded/Unloaded、
        /// 窗口可见性变化时各配一次）。
        /// </summary>
        public static void AcquireWatch()
        {
            lock (_wakeGate)
            {
                _watcherCount++;
                if (_watcherCount == 1)
                {
                    // 从「停」切到「跑」：立即唤醒采样线程，
                    // 让用户切回来的第一眼就是新数据，而不是空白或陈数据。
                    _watchSignal.Set();
                    LogService.Info("进程快照采样：已恢复（列表可见）");
                }
            }
        }

        /// <summary>释放观察者。计数归零后采样线程挂起，不再消耗 CPU。</summary>
        public static void ReleaseWatch()
        {
            lock (_wakeGate)
            {
                if (_watcherCount == 0)
                    return;

                _watcherCount--;
                if (_watcherCount == 0)
                {
                    // 这里不需要额外清信号：AutoResetEvent 是一次性的，
                    // 采样线程下一轮 Wait 时自然就睡下去了（最多多醒一次，
                    // 醒来后 IsWatched 为 false，随即继续等待）。
                    LogService.Info("进程快照采样：已挂起（列表不可见）");
                }
            }
        }

        /// <summary>
        /// 宿主窗口是否可见。false = 窗口已隐藏 / 最小化 / 收进托盘。
        ///
        /// 【为什么还要这一层，不靠引用计数就够了？】
        /// 引用计数只能反映「有没有人主动在看列表」，反映不了「窗口根本看不见」。
        /// 窗口隐藏后，即使某个页面因为异常路径忘了 ReleaseWatch，
        /// 采样也必须停 —— 这里是硬闸门，不依赖任何人的 bookkeeping。
        ///
        /// 恢复时立刻唤醒采样线程：用户把窗口叫回来时，第一眼就该是新数据。
        /// </summary>
        public static void SetHostVisible(bool visible)
        {
            if (_hostVisible == visible)
                return;

            _hostVisible = visible;

            if (visible)
                _watchSignal.Set();
        }

        /// <summary>宿主窗口当前是否可见（自检 / 日志用）。</summary>
        public static bool HostVisible => _hostVisible;

        /// <summary>CPU 占用滑动窗口的长度（秒）。</summary>
        private const double CpuWindowSeconds = 30.0;

        /// <summary>窗口最多保留的采样点数，防止某个周期被拖长时队列膨胀。</summary>
        private const int MaxWindowPoints = 64;

        /// <summary>路径解析失败后的重试间隔（秒）。</summary>
        private const double PathRetrySeconds = 30.0;

        /// <summary>最新快照（只读副本）。</summary>
        public static IReadOnlyList<Entry> Latest
        {
            get { lock (_gate) return _latest.ToArray(); }
        }

        /// <summary>
        /// 采样完成事件（携带本轮快照）。
        ///
        /// 【在采样线程（STA 后台线程）上同步触发】
        /// 订阅方必须自行 Dispatcher.BeginInvoke 回 UI 线程再动任何控件或集合。
        /// 这里不做封送，是因为本类要能在没有 Dispatcher 的纯后台模式（-silent）下工作。
        /// </summary>
        public static event Action<IReadOnlyList<Entry>>? Refreshed;

        /// <summary>采样周期（秒）。
        ///
        /// 默认 5 秒。这个值在 1.0.1 从 3 秒放宽：
        /// 一是列表每 3 秒重排一次，用户伸手去点某一行的下拉框时经常点空；
        /// 二是「用户在看列表」这件事本身不需要秒级精度 —— 进程占用是用来
        /// 判断「谁在吃 CPU」的，5 秒足够看出趋势，而 3 秒只是在放大噪声。
        /// 10 秒试过，滞后感明显：刚打开的进程要等近十秒才出现在列表里。
        ///
        /// 单帧开销没变（Toolhelp32 快照 + 每进程 OpenProcess 取时间），
        /// 所以放宽周期等于把后台占用直接降到约六成（3 → 5 秒）。
        ///
        /// 这个周期只在「用户真的在看列表」时生效 —— 没人看时循环完全挂起
        /// （见 AcquireWatch / ReleaseWatch），不再是「降频到某个慢周期」。
        /// 自动绑定与此无关，那条链路走 ProcessWatcher 的 WMI 事件。
        ///
        /// 想更灵敏就调小（最小 1 秒），想更省就调大。改这里一处即可，
        /// 全工程没有第二个地方写死采样周期。
        ///
        /// volatile：UI 线程写、后台采样线程读，不加会让循环可能长期读到缓存里的旧值。</summary>
        private static volatile int _intervalSeconds = 5;
        public static int IntervalSeconds
        {
            get => _intervalSeconds;
            set => _intervalSeconds = Math.Max(1, value);
        }

        /// <summary>启动后台采样线程（幂等）。应用启动时调用一次即可。
        ///
        /// 线程启动后并不等于立刻开始采集：没人观察时它会挂在信号量上等待，
        /// 真正的采集频率由「有没有人在看」决定（见 AcquireWatch / ReleaseWatch）。
        /// </summary>
        public static void Start()
        {
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1)
                return;

            CancellationTokenSource cts = new();
            _cts = cts;

            Thread thread = new(() => RunLoop(cts.Token))
            {
                IsBackground = true,
                Name = "ZenForge.ProcessSampler",
                // 采样属于「后台维护」工作，绝不该和 UI 抢时间片。
                Priority = ThreadPriority.BelowNormal,
            };

            // 【必须是 STA】
            // 见 _watchSignal 的注释：MTA 线程上一旦碰到 WPF 对象就是
            //「调用线程必须为 STA」。把它设成 STA 之后，这类问题从「必然崩溃」
            // 变成「只是不该这么做」，采样链路本身也不会再因为单元状态被带崩。
            thread.SetApartmentState(ApartmentState.STA);
            _samplerThread = thread;
            thread.Start();
        }

        /// <summary>
        /// 请求「现在立刻采一帧」，用于刚打开列表时不要干等一个完整周期。
        ///
        /// 排队而不是直接同步执行：采样必须在采样线程上跑（STA + 串行），
        /// 在 UI 线程上同步 Collect 会把几百次 OpenProcess 压到界面上。
        /// </summary>
        public static void RequestSample()
        {
            try
            {
                _sampleNowSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已经有一个待处理的请求，合并即可
            }
        }

        private static void RunLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 没人看 / 窗口不可见：挂在事件上，CPU 占用为零。
                    // 取消时 Stop() 会 Set 一次把它踢出。
                    //
                    // 【两种信号必须一起等】
                    // ① _watchSignal：有人开始看列表；
                    // ② _sampleNowSignal：有人点名要一帧（降级轮询的补扫会用）。
                    // 只等 ① 的话，② 会在「没人看」时被丢掉，调用方只能白等一个超时，
                    // 拿到的还是上一份快照 —— 这正是降级模式下补扫失效的原因。
                    if (!IsWatched || !_hostVisible)
                    {
                        int index = WaitHandle.WaitAny(
                            new[]
                            {
                                _watchSignal,
                                _sampleNowSignal.AvailableWaitHandle,
                                token.WaitHandle,
                            },
                            Timeout.Infinite);

                        if (index == 2)
                            break;                                  // 取消

                        if (index == 1)
                        {
                            // 有人点名要一帧：消费掉请求并采。
                            while (_sampleNowSignal.Wait(0))
                            {
                                // 消费完毕
                            }

                            SampleOnce("立即采样请求");
                        }

                        continue;
                    }

                    // 有人看：到点就采一帧，或被打断（有人要求立即采）。
                    SampleOnce("周期刷新");

                    if (!WaitInterval(token))
                        break;
                }
                catch (ObjectDisposedException)
                {
                    // 退出阶段事件被释放，正常收尾
                    break;
                }
            }
        }

        /// <summary>
        /// 等满一个采样周期，或被「立即采样」请求提前唤醒。
        ///
        /// 用定时器而不是 Task.Delay：这条线程上没有同步上下文，用 async/await
        /// 只会把执行流甩回线程池（又变回 MTA），那正是要避免的事。
        /// </summary>
        private static bool WaitInterval(CancellationToken token)
        {
            // 进睡眠前清掉残留请求：不清的话上一轮的「立即采样」会让这一轮立刻返回，
            // 从 3 秒一次退化成紧密循环。
            while (_sampleNowSignal.Wait(0))
            {
                // 消费完毕
            }

            using Timer ticker = new(
                _ => TryRelease(_sampleNowSignal), null,
                TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds)), Timeout.InfiniteTimeSpan);

            int index = WaitHandle.WaitAny(
                new[]
                {
                    _sampleNowSignal.AvailableWaitHandle,
                    _watchSignal,
                    token.WaitHandle,
                },
                Timeout.Infinite);

            // 0 = 立即采样 / 定时到点，1 = 观察者状态变化（进页面或离开，离开时下一轮会挂起）
            return index is 0 or 1;
        }

        /// <summary>采一帧并广播；异常只记 WARN，绝不让循环停摆。</summary>
        private static void SampleOnce(string reason)
        {
            try
            {
                Sample();
            }
            catch (Exception ex)
            {
                // WARN 而不是 ERROR：单轮采样失败（某个进程刚退出、句柄失效）
                // 下一秒就会自愈，它不构成「操作失败」，只是「这一轮没采全」。
                // 记 ERROR 会让日志页红一片，把真正的失败埋掉。
                LogService.Warn($"进程快照失败（{reason}，本轮已跳过）: {ex.Message}");
            }
            finally
            {
                CompleteWaiters();
            }
        }

        /// <summary>停止后台采样。</summary>
        public static void Stop()
        {
            Interlocked.Exchange(ref _loopRunning, 0);

            CancellationTokenSource? cts = _cts;
            _cts = null;

            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch
            {
                // 已释放等情况忽略
            }

            // 循环可能正挂在信号量上等待，光 Cancel 不会让它醒来，必须补一次信号。
            _watchSignal.Set();
            TryRelease(_sampleNowSignal);

            cts.Dispose();
        }

        private static void TryRelease(SemaphoreSlim semaphore)
        {
            try
            {
                semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // 计数已满说明本来就有待消费的信号，无需再补
            }
        }

        /// <summary>调整采样周期（搜索框聚焦时加速）。下一轮循环生效。</summary>
        public static void SetInterval(int seconds) => IntervalSeconds = Math.Max(1, seconds);

        /// <summary>
        /// 执行一次采样并广播结果。
        ///
        /// 【公开方法，但只应在采样线程上调用】
        /// 唯一的例外是启动补扫（ProcessWatcher.TakeSnapshotForScan），它需要
        /// 「同步拿到结果」。那条路径在 WMI 回调线程上，同样是 MTA —— 所以它
        /// 现在也改成走 <see cref="SampleOnSamplerThread"/>。
        /// </summary>
        public static void Sample()
        {
            List<Entry> collected;
            try
            {
                collected = Collect();
            }
            catch (Exception ex)
            {
                LogService.Warn($"进程快照失败（本次请求已跳过）: {ex.Message}");
                return;
            }

            lock (_gate)
            {
                _latest.Clear();
                _latest.AddRange(collected);
            }

            // 注意：本事件在采样线程上触发，订阅方必须自行切回 UI 线程。
            Refreshed?.Invoke(collected);
        }

        /// <summary>
        /// 在采样线程上同步跑一次采样，并等它完成。
        ///
        /// 给「启动补扫」这类需要结果立刻可用的调用方用：它不能等 3 秒周期，
        /// 也不该自己在调用线程上跑（那条线程可能是 MTA 的 WMI 回调）。
        /// </summary>
        public static IReadOnlyList<Entry> SampleOnSamplerThread(TimeSpan timeout)
        {
            using ManualResetEventSlim done = new(false);

            // 把「采一帧 + 通知完成」打包成一个请求：请求队列每被消费一次，
            // 就说明采样线程已经处理到它了，此时 Latest 一定是最新的。
            lock (_pendingWaiters)
                _pendingWaiters.Enqueue(done);

            RequestSample();

            done.Wait(timeout);
            return Latest;
        }

        /// <summary>等待中的同步调用方。采样线程每完成一帧就把它们全部唤醒。</summary>
        private static readonly Queue<ManualResetEventSlim> _pendingWaiters = new();

        private static void CompleteWaiters()
        {
            lock (_pendingWaiters)
            {
                while (_pendingWaiters.Count > 0)
                {
                    try
                    {
                        _pendingWaiters.Dequeue().Set();
                    }
                    catch
                    {
                        // 调用方可能已经超时释放了事件，忽略
                    }
                }
            }
        }

        /// <summary>执行一次快照采集（单次 Toolhelp 遍历 + 按需查路径）。</summary>
        private static List<Entry> Collect()
        {
            List<Entry> result = new();
            long now = Stopwatch.GetTimestamp();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(INVALID_HANDLE_VALUE))
                return result;

            try
            {
                PROCESSENTRY32 entry = new() { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };

                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        string name = entry.szExeFile ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name += ".exe";

                        int pid = (int)entry.th32ProcessID;

                        ProcSample sample;
                        lock (_sampleGate)
                            sample = GetOrCreateSample(pid);

                        double cpu = 0.0;
                        if (ReadProcessTimes(pid, sample))
                            cpu = ComputeCpu(sample, now);

                        ResolvePath(pid, sample, now);

                        result.Add(new Entry
                        {
                            Name = name,
                            Pid = pid,
                            Path = sample.Path,
                            Cpu = cpu,
                            PriorityClass = entry.pcPriClassBase
                        });
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            PruneDeadSamples(result);

            return result;
        }

        /// <summary>
        /// 用滑动窗口算平均 CPU 占用（0-100）。
        ///
        /// 旧实现拿「本轮累计 - 上轮累计」除以「本轮时刻 - 上轮时刻」做两点差分：
        /// 采样间隔只要抖一下（GC、系统忙、定时器不准），算出来的百分比就会大幅跳动，
        /// 而列表默认按占用降序排，于是整张表每 2 秒重排一次，行不停跳位。
        ///
        /// 改成维护一个 30 秒窗口，每次用窗口里<b>最老的那一点</b>与当前比，
        /// 得到的是这整段时间的平均值，短时抖动被摊平，排序才站得住。
        /// </summary>
        private static double ComputeCpu(ProcSample sample, long now)
        {
            long total = sample.KernelTicks + sample.UserTicks;
            sample.CpuWindow.Enqueue((now, total));

            long windowTicks = (long)(CpuWindowSeconds * Stopwatch.Frequency);
            while (sample.CpuWindow.Count > 0 && now - sample.CpuWindow.Peek().Stamp > windowTicks)
                sample.CpuWindow.Dequeue();

            while (sample.CpuWindow.Count > MaxWindowPoints)
                sample.CpuWindow.Dequeue();

            if (sample.CpuWindow.Count < 2)
                return 0.0;

            (long Stamp, long Ticks) oldest = sample.CpuWindow.Peek();

            long elapsedStamp = now - oldest.Stamp;
            long cpuTicks = total - oldest.Ticks;
            if (elapsedStamp <= 0 || cpuTicks <= 0)
                return 0.0;

            // FILETIME 的单位是 100ns，与 TimeSpan 的 tick 一致，这里显式换算。
            // 旧代码用 Stopwatch.Frequency 去除 FILETIME 计数 —— 在 Windows 上这两者
            // 恰好都是 10^7，所以「碰巧算对」，但那是巧合，不是定义。
            double usedSeconds = TimeSpan.FromTicks(cpuTicks).TotalSeconds;
            double elapsedSeconds = elapsedStamp / (double)Stopwatch.Frequency;
            if (elapsedSeconds <= 0)
                return 0.0;

            int processorCount = Math.Max(1, Environment.ProcessorCount);
            double pct = usedSeconds / elapsedSeconds / processorCount * 100.0;

            return pct < 0 ? 0 : (pct > 100 ? 100 : pct);
        }

        /// <summary>
        /// 解析路径，失败后按 <see cref="PathRetrySeconds"/> 退避重试。
        ///
        /// 旧写法把 PathResolved 无条件置 true，查回来是空串也算「已解析」，
        /// 于是进程刚被创建的那一瞬间取不到路径，这条记录就永久留空 ——
        /// 界面上表现为大片「路径未记录」，而且重启软件前救不回来。
        ///
        /// 绝大多数失败是时序问题（进程刚创建、句柄还没准备好），不是权限问题，
        /// 所以失败后延迟重试通常第二次就成功；受保护进程（反作弊、PPL）属于真失败，
        /// 会按同样节奏重试但不纠缠到耗尽 CPU。
        /// </summary>
        private static void ResolvePath(int pid, ProcSample sample, long now)
        {
            if (sample.PathResolved)
                return;

            long retryTicks = (long)(PathRetrySeconds * Stopwatch.Frequency);
            if (sample.PathFailStamp != 0 && now - sample.PathFailStamp < retryTicks)
                return;

            string path = QueryImagePath(pid);
            if (path.Length > 0)
            {
                sample.Path = path;
                sample.PathResolved = true;
                sample.PathFailStamp = 0;
            }
            else
            {
                sample.PathFailStamp = now;
            }
        }

        /// <summary>
        /// 清掉已退出进程的采样记录。
        ///
        /// 顺带解决 PID 复用：每个新 PID 都会拿到全新的 ProcSample，
        /// 不会继承上一个同 PID 进程的路径、失败状态或 CPU 窗口。
        /// </summary>
        private static void PruneDeadSamples(List<Entry> result)
        {
            lock (_sampleGate)
            {
                // 每个 Entry 持有一个 30 秒的 CPU 采样窗口（最多 MaxWindowPoints 个点），
                // 进程退出后这份对象没人再引用它，留着就是纯泄漏。
                //
                // 旧阈值 4096 的问题：日常机器上同时存在的 PID 通常只有几百，
                // 永远到不了 4096，等于这段清理从不执行 —— 开机一天后 _samples 里
                // 会积压成千上万个已退出进程的空壳。改成「样本数明显多于本轮存活数」
                // 就扫一次，代价是每轮一次 HashSet 构建（几百个 int，微秒级）。
                if (_samples.Count <= result.Count)
                    return;

                HashSet<int> alive = new(result.Select(e => e.Pid));
                List<int> dead = _samples.Keys.Where(k => !alive.Contains(k)).ToList();
                foreach (int k in dead)
                    _samples.Remove(k);
            }
        }

        private static ProcSample GetOrCreateSample(int pid)
        {
            if (!_samples.TryGetValue(pid, out ProcSample? s))
            {
                s = new ProcSample();
                _samples[pid] = s;
            }
            return s;
        }

        /// <summary>读取进程时间片（内核 + 用户）。</summary>
        private static bool ReadProcessTimes(int pid, ProcSample sample)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
                return false;

            try
            {
                if (!GetProcessTimes(handle, out _, out _, out long kernel, out long user))
                    return false;

                sample.KernelTicks = kernel;
                sample.UserTicks = user;
                sample.StampTicks = Stopwatch.GetTimestamp();
                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>取进程可执行文件完整路径（权限不足时返回空串）。</summary>
        private static string QueryImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
                return string.Empty;

            try
            {
                StringBuilder sb = new(1024);
                uint size = (uint)sb.Capacity;

                return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
