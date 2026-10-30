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

        /// <summary>保护 <see cref="_samples"/>：单例字典会被采样线程与任何外部 Sample 调用同时接触。</summary>
        private static readonly object _sampleGate = new();

        private static CancellationTokenSource? _cts;
        private static int _loopRunning;

        /// <summary>
        /// 锁住 <see cref="_watcherCount"/> 的增减与唤醒信号。
        /// </summary>
        private static readonly object _wakeGate = new();

        /// <summary>
        /// 采集循环的唤醒信号。有人在看 → 每轮 Delay 超时自然醒来；
        /// 没人看 → 循环在这里无限期等待，CPU 占用为零。
        /// 用信号量而不是轮询标志，是为了「切回页面」时能立即唤醒，不必等到下个周期。
        /// </summary>
        private static readonly SemaphoreSlim _wakeSignal = new(0);

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
                    // 从「停」切到「跑」：立即唤醒循环，并补一次采样，
                    // 让用户切回来的第一眼就是新数据，而不是空白或陈数据。
                    _wakeSignal.Release();
                    LogService.Info("进程快照采样：已恢复（列表可见）");
                }
            }
        }

        /// <summary>释放观察者。计数归零后采样循环会挂起，不再消耗 CPU。</summary>
        public static void ReleaseWatch()
        {
            lock (_wakeGate)
            {
                if (_watcherCount == 0)
                    return;

                _watcherCount--;
                if (_watcherCount == 0)
                {
                    LogService.Info("进程快照采样：已挂起（列表不可见）");
                }
            }
        }

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

        /// <summary>采样完成事件（携带本轮快照）。订阅方需自行切回 UI 线程。</summary>
        public static event Action<IReadOnlyList<Entry>>? Refreshed;

        /// <summary>采样周期（秒）。设为更小值时由搜索框聚焦场景驱动。
        ///
        /// 默认 3 秒：进程列表刷新是 UI 侧最耗 CPU 的例程（Toolhelp32 快照 + 每进程
        /// OpenProcess 取时间），数百进程规模下约占 1~3% 单核。
        ///
        /// 这个周期只在「用户真的在看列表」时生效 —— 没人看时循环完全挂起
        /// （见 AcquireWatch / ReleaseWatch），不再是「降频到某个慢周期」。
        /// 自动绑定与此无关，那条链路走 ProcessWatcher 的 WMI 事件。
        ///
        /// volatile：UI 线程写、后台采样循环读，不加会让循环可能长期读到缓存里的旧值。</summary>
        private static volatile int _intervalSeconds = 3;
        public static int IntervalSeconds
        {
            get => _intervalSeconds;
            set => _intervalSeconds = value;
        }

        /// <summary>启动后台采样循环（幂等）。应用启动时调用一次即可。
        ///
        /// 循环启动后并不等于立刻开始采集：没人观察时它会挂在 _wakeSignal 上等待，
        /// 真正的采集频率由「有没有人在看」决定（见 AcquireWatch / ReleaseWatch）。
        /// </summary>
        public static void Start()
        {
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1)
                return;

            CancellationTokenSource cts = new();
            _cts = cts;

            Task.Run(() => RunLoopAsync(cts.Token), cts.Token);
        }

        private static async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!IsWatched)
                {
                    if (!await WaitForWatchersAsync(token).ConfigureAwait(false))
                        break;

                    // 唤醒后可能又是「没人看」（Stop 引起的取消），这一轮就不采了
                    if (!IsWatched)
                        continue;
                }

                try
                {
                    Sample();
                }
                catch (Exception ex)
                {
                    // 循环体本身不能因为一次异常就停摆
                    LogService.Error($"进程快照失败: {ex.Message}");
                }

                if (!await WaitForNextRoundAsync(token).ConfigureAwait(false))
                    break;
            }
        }

        /// <summary>没人看时挂在信号量上等待，返回 false 表示循环该退出。</summary>
        private static async Task<bool> WaitForWatchersAsync(CancellationToken token)
        {
            // 用等待信号量而不是「睡一个很长的时间」：
            // 后者在用户切回页面的瞬间无法立刻响应，还得等睡完。
            try
            {
                await _wakeSignal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return !token.IsCancellationRequested;
        }

        /// <summary>等满一个周期，或被「有人切回列表」提前唤醒。返回 false 表示循环该退出。</summary>
        private static async Task<bool> WaitForNextRoundAsync(CancellationToken token)
        {
            int seconds = Math.Max(1, IntervalSeconds);

            // 进入睡眠前先把信号量清零：上一轮可能因为「提前唤醒」
            // 残留了若干个未消费的信号，不清掉会让后续每一轮都立即返回，
            // 从 3 秒一次退化成紧密循环。
            while (_wakeSignal.Wait(0))
            {
                // 消费完毕
            }

            using CancellationTokenSource roundCts =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            Task delay = Task.Delay(TimeSpan.FromSeconds(seconds), roundCts.Token);
            Task signal = _wakeSignal.WaitAsync(roundCts.Token);

            try
            {
                await Task.WhenAny(delay, signal).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                // 撤销本轮未完成的等待，避免残留的 WaitAsync 在
                // 下一轮被误当成「新的唤醒」（也避免任务越积越多）
                roundCts.Cancel();
            }

            return !token.IsCancellationRequested;
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

            // 循环可能正挂在信号量上等待（没人看列表），光 Cancel 不会让它醒来，
            // 必须补一次 Release 把 WaitAsync 踢出，否则线程会一直挂着。
            try
            {
                _wakeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 计数已满说明本来就有待消费的信号，无需再补
            }

            cts.Dispose();
        }

        /// <summary>调整采样周期（搜索框聚焦时加速）。下一轮循环生效。</summary>
        public static void SetInterval(int seconds)
        {
            IntervalSeconds = Math.Max(1, seconds);
        }

        /// <summary>执行一次采样并广播结果。可由后台循环或外部调用。</summary>
        public static void Sample()
        {
            List<Entry> collected;
            try
            {
                collected = Collect();
            }
            catch (Exception ex)
            {
                LogService.Error($"进程快照失败: {ex.Message}");
                return;
            }

            lock (_gate)
            {
                _latest.Clear();
                _latest.AddRange(collected);
            }

            // 注意：本事件在后台线程触发，订阅方必须自行切回 UI 线程。
            Refreshed?.Invoke(collected);
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
