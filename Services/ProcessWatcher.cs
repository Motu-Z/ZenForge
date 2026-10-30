using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 进程绑核后台监听。
    ///
    /// 主路径订阅 WMI 的 __InstanceCreationEvent，内核报「有新进程」才处理该 PID；
    /// WMI 不可用（精简系统删了 Winmgmt）时降级为 3 秒轮询，并弹窗告知而不是静默不工作。
    /// 规则按进程名匹配，结果一律进日志。
    ///
    /// 为什么不再定时轮询：旧实现每秒枚举全系统进程表（Toolhelp32 + 每进程 OpenProcess），
    /// 300 进程下是 1~3% 单核常驻开销，而它 99% 的时间只是在确认「没有新进程」。
    ///
    /// 关键设计：用 PidSet 记录已处理 PID 避免重复绑定；两种模式都清理已退出 PID
    /// 防止 PID 复用漏绑；WMI 回调不在 UI 线程；异常一律吞掉记日志；不碰电源与 SMU。
    /// </summary>
    public static class ProcessWatcher
    {
        private static readonly object _lock = new();

        private static CancellationTokenSource? _cts;
        private static System.Threading.Tasks.Task? _loop;

        /// <summary>WMI 事件订阅器（事件模式下非 null）。</summary>
        private static ManagementEventWatcher? _creationWatcher;
        private static ManagementEventWatcher? _deletionWatcher;

        /// <summary>
        /// 当前运行模式。true = WMI 事件驱动（首选）；false = 定时轮询（降级）。
        /// 供设置页/日志展示，也让 Start() 的幂等判断能区分模式。
        /// </summary>
        public static bool UsingEvents { get; private set; }

        /// <summary>
        /// 已尝试绑定的 PID（成功或失败都记，避免重复尝试刷日志）。
        ///
        /// 用带锁容器而不是裸 HashSet：WMI 回调线程与降级轮询线程都可能读写它，
        /// UI 线程还会在保存规则后调 ResetCache() 清空。不加锁时 Clear 与
        /// Add 并发会让 HashSet 内部桶状态错乱甚至抛 InvalidOperationException。
        /// </summary>
        private static readonly PidSet _handledPids = new();

        /// <summary>
        /// 降级轮询间隔（毫秒）。只在 WMI 不可用时使用，因此取宽松值：
        /// 既然已经退回轮询，就优先照顾 CPU 占用而不是及时性。
        /// </summary>
        private const int FallbackIntervalMs = 3000;

        /// <summary>
        /// 全局互斥体名：保证同一台机器上只有一个 ZenForge 进程在跑绑核监听。
        /// </summary>
        private const string MutexName = @"Global\ZenForge.ProcessWatcher.Singleton";

        private static Mutex? _mutex;

        /// <summary>监听是否处于运行状态（两种模式共用）。</summary>
        private static bool _running;

        public static bool HasOwnership => _mutex != null;

        /// <summary>
        /// WMI 不可用时的降级提示动作。由 UI 层注入；纯后台不注入，降级时只记日志。
        ///
        /// 用 Action 而不是直接调 DarkDialog，是为了让本类保持零 WPF 依赖 ——
        /// 它是纯后台进程里唯一真正干活的组件，不能因为一句提示把整个
        /// PresentationFramework 拖进来。
        /// </summary>
        public static Action? WmiUnavailableNotifier { get; set; }

        /// <summary>
        /// 是否正在监听。不能只看 _loop：事件模式下的 _loop 是「启动补扫任务」，
        /// 跑完就 completed，而监听本身仍在进行（由 WMI 订阅维持）。
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                lock (_lock)
                    return _running;
            }
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_running)
                    return;

                if (!TryAcquireMutex())
                {
                    LogService.Warn(
                        "已有另一个 ZenForge 实例在运行进程绑核监听，本实例跳过（避免重复绑定）");
                    return;
                }

                _handledPids.Clear();

                // 先试事件模式。失败不抛，只是把 UsingEvents 置 false，
                // 由下面统一走降级轮询。
                UsingEvents = TryStartEventMode();

                if (UsingEvents)
                {
                    // 事件模式下不再有轮询循环。补一次「当前已有进程」的全量扫描，
                    // 因为 WMI 事件只覆盖订阅之后发生的创建，启动前就在跑的程序
                    // （浏览器、游戏启动器）不会触发事件。
                    _loop = System.Threading.Tasks.Task.Run(InitialScan);
                    _running = true;

                    LogService.Info("进程绑核监听已启动（WMI 事件驱动，空闲零轮询）");
                }
                else
                {
                    _cts = new CancellationTokenSource();
                    CancellationToken token = _cts.Token;
                    _loop = System.Threading.Tasks.Task.Run(() => LoopAsync(token), token);
                    _running = true;

                    LogService.Warn(
                        $"WMI 不可用，进程绑核监听已降级为定时轮询（每 {FallbackIntervalMs / 1000} 秒一次）");

                    // 注意：不能在这里直接 ShowWarning —— 当前持有 _lock，模态框会
                    // 一直阻塞到用户点确定，期间任何 Stop()/ResetCache() 都被卡住。
                    // 改为投递到 UI 队列，等 Start() 返回、锁释放后再弹。
                    NotifyWmiUnavailableAsync();
                }
            }
        }

        /// <summary>
        /// 提示 WMI 不可用。只在应用有主窗口时弹（静默自启没有窗口，
        /// 弹模态框会漂在桌面上无人可关，还挡住后台常驻）。
        /// </summary>
        private static void NotifyWmiUnavailableAsync()
        {
            try
            {
                // 提示动作由 UI 层注入。ProcessWatcher 是纯后台核心，
                // 绝不能直接引用 App / DarkDialog —— 那会把 WPF 拖进常驻进程，
                // 轻量后台的整个前提就没了。
                Action? notifier = WmiUnavailableNotifier;
                if (notifier != null)
                {
                    notifier();
                    return;
                }

                LogService.Warn("WMI 不可用，进程绑核已降级为定时轮询（无界面，未弹提示）");
            }
            catch (Exception ex)
            {
                LogService.Warn($"显示监听降级提示失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 尝试订阅 WMI 进程创建/删除事件。任何一步失败都返回 false（不抛）。
        ///
        /// 查询只取需要的两个属性：SELECT * 会把 CommandLine、ExecutablePath 等
        /// 全部序列化过来，而这些字段我们一个都不用（路径自己用 QueryFullProcessImageName
        /// 取更快也更准）。限定列名能显著降低 WMI 侧的编码与解析开销。
        /// </summary>
        private static bool TryStartEventMode()
        {
            // 事件查询的 SELECT 列表里不能出现 "TargetInstance.Xxx" 这种带点号的
            // 属性路径 —— 那是普通实例查询的语法，用在 __Instance*Event 上
            // WqlEventQuery 会直接抛「无效的参数」。
            // 事件查询只能选 * 或者 TargetInstance 整体，字段在事件到达后再取，
            // 反正拿到的是已经物化好的对象，不需要再查一次 WMI。
            //
            // WITHIN 2 而不是 1：WITHIN 是 WMI 服务内部的轮询粒度，越小越耗 CPU。
            // 2 秒对「进程启动后多久被绑定」几乎无感，却能把 WMI 的常驻开销减半。
            const string CreationQuery =
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_Process'";

            const string DeletionQuery =
                "SELECT * FROM __InstanceDeletionEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_Process'";

            try
            {
                // 订阅前先做一次连通性探测：事件查询失败往往要等到 Start() 才抛，
                // 提前用一次轻量查询把这些情况（服务停用、权限不足）识别出来，
                // 避免建好一半订阅器又得回滚。
                using (ManagementObjectSearcher probe = new("SELECT ProcessId FROM Win32_Process WHERE ProcessId = 4"))
                {
                    using ManagementObjectCollection results = probe.Get();
                }

                ManagementEventWatcher creation = new(new WqlEventQuery(CreationQuery));
                creation.EventArrived += OnProcessCreated;

                ManagementEventWatcher deletion = new(new WqlEventQuery(DeletionQuery));
                deletion.EventArrived += OnProcessExited;

                try
                {
                    creation.Start();
                    try
                    {
                        deletion.Start();
                    }
                    catch
                    {
                        // 创建订阅成功、删除订阅失败：退掉已建的一半，整体降级。
                        // creation 已 Start，需要 Stop 后 Dispose；deletion 未 Start
                        // 但对象已创建且挂了事件，同样要解绑并释放，否则泄漏。
                        creation.EventArrived -= OnProcessCreated;
                        try { creation.Stop(); } catch { /* 已停止 */ }
                        creation.Dispose();
                        throw;
                    }
                }
                catch
                {
                    deletion.EventArrived -= OnProcessExited;
                    deletion.Dispose();
                    throw;
                }

                _creationWatcher = creation;
                _deletionWatcher = deletion;
                return true;
            }
            catch (Exception ex)
            {
                LogService.Warn($"WMI 事件订阅失败：{ex.Message}");
                return false;
            }
        }

        private static void StopEventMode()
        {
            ManagementEventWatcher? creation = _creationWatcher;
            ManagementEventWatcher? deletion = _deletionWatcher;
            _creationWatcher = null;
            _deletionWatcher = null;

            if (creation != null)
            {
                creation.EventArrived -= OnProcessCreated;
                try { creation.Stop(); } catch { /* 未启动时 Stop 会抛，忽略 */ }
                creation.Dispose();
            }

            if (deletion != null)
            {
                deletion.EventArrived -= OnProcessExited;
                try { deletion.Stop(); } catch { /* 同上 */ }
                deletion.Dispose();
            }
        }

        private static bool TryAcquireMutex()
        {
            if (_mutex != null)
                return true;

            try
            {
                Mutex mutex = new(initiallyOwned: false, MutexName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    mutex.Dispose();
                    return false;
                }

                _mutex = mutex;
                return true;
            }
            catch (Exception ex)
            {
                LogService.Warn($"创建监听互斥体失败（降级为无保护运行）：{ex.Message}");
                return true;
            }
        }

        private static void ReleaseMutex()
        {
            if (_mutex == null)
                return;

            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // 未持有时释放会抛异常，忽略
            }

            try
            {
                _mutex.Dispose();
            }
            catch
            {
                // 忽略
            }

            _mutex = null;
        }

        public static void Stop()
        {
            lock (_lock)
            {
                // 用 _running 判断，而不是 _cts / _creationWatcher：
                // 事件模式下 _cts 恒为 null，而 _loop 跑完就 completed，两者都不能代表「在跑」。
                if (!_running)
                    return;

                StopEventMode();

                if (_cts != null)
                {
                    try
                    {
                        _cts.Cancel();
                    }
                    catch
                    {
                        // 忽略
                    }

                    _cts.Dispose();
                    _cts = null;
                }

                _loop = null;
                _running = false;
                UsingEvents = false;
                _handledPids.Clear();

                ReleaseMutex();
            }

            LogService.Info("进程绑核监听已停止");
        }

        /// <summary>清空「已处理」缓存，让规则立即重新应用（保存规则后调用）。</summary>
        public static void ResetCache()
        {
            lock (_lock)
            {
                _handledPids.Clear();
            }

            // 同时让规则缓存失效，保证下一次查找读到刚保存的内容
            InvalidateRuleCache();
        }

        // ==================== WMI 事件回调 ====================

        /// <summary>
        /// 从 WMI 事件对象里安全地取出 PID。
        ///
        /// ProcessId 在 WQL 结果里的实际类型是 uint，直接 Convert.ToInt32 遇到
        /// DBNull（进程刚好在查询与投递之间退出）还会抛 InvalidCastException。
        /// 事件是高频路径，不该靠外层 catch 处理这种可预期的缺失。
        /// </summary>
        private static int ReadPid(ManagementBaseObject target)
        {
            object? raw = null;
            try
            {
                raw = target["ProcessId"];
            }
            catch
            {
                return 0;
            }

            if (raw == null || raw is DBNull)
                return 0;

            try
            {
                return Convert.ToInt32(raw);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 新进程创建。回调在 WMI 自己的工作线程上，不能碰 UI。
        /// </summary>
        private static void OnProcessCreated(object sender, EventArrivedEventArgs e)
        {
            try
            {
                if (e.NewEvent?["TargetInstance"] is not ManagementBaseObject target)
                    return;

                int pid = ReadPid(target);
                string name = target["Name"] as string ?? string.Empty;
                if (pid <= 0)
                    return;

                HandleProcess(pid, name);
            }
            catch (Exception ex)
            {
                // 单个进程处理失败不能影响订阅本身
                LogService.Warn($"处理新建进程事件失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 进程退出。只做 PID 回收，避免 PID 复用后被误判为「已绑定」而跳过。
        /// </summary>
        private static void OnProcessExited(object sender, EventArrivedEventArgs e)
        {
            try
            {
                if (e.NewEvent?["TargetInstance"] is not ManagementBaseObject target)
                    return;

                int pid = ReadPid(target);
                if (pid > 0)
                    _handledPids.Remove(pid);
            }
            catch
            {
                // 淘汰失败无实际影响：下一轮 PruneTo 还会兜底清理
            }
        }

        // ==================== 核心：单个进程的处理 ====================

        /// <summary>
        /// 处理一个进程：查规则 → 命中且未处理则下发 CPU Set。
        /// 事件模式与轮询模式共用这一条路径。
        /// </summary>
        private static void HandleProcess(int pid, string name)
        {
            if (pid <= 0 || string.IsNullOrEmpty(name))
                return;

            // 已有记录直接跳过。这一步不是 TryAdd 的冗余：规则查询（FindRuleFor）要遍历
            // 整份规则表，先挡掉已处理的 PID 能省掉绝大多数无谓查询。
            if (_handledPids.Contains(pid))
                return;

            BindingRule? rule = FindRuleFor(name);
            if (rule == null)
                return;

            // 命中规则才记录。TryAdd 保证「查 + 写」原子，
            // 万一同一进程被两条路径同时处理，只有一方能下发。
            if (!_handledPids.TryAdd(pid))
                return;

            // 自定义掩码：优先取实时掩码，缺失时回退快照
            ulong mask = rule.Target == BindingTarget.Custom
                ? (MaskStore.FindById(rule.MaskId)?.Mask ?? rule.Mask)
                : 0;

            CpuSetBindResult result;
            string label;

            switch (rule.Target)
            {
                case BindingTarget.CCD0:
                    result = CpuSetBinder.BindToCcd(pid, 0);
                    label = "CCD0";
                    break;
                case BindingTarget.CCD1:
                    result = CpuSetBinder.BindToCcd(pid, 1);
                    label = "CCD1";
                    break;
                default:
                    result = CpuSetBinder.BindToMask(pid, mask);
                    label = rule.Label;
                    break;
            }

            if (result.Success)
            {
                LogService.Info(
                    $"已绑定 {name}（PID {pid}）→ {label}，{result.AppliedCount} 个 CPU Set");
            }
            else
            {
                LogService.Warn($"绑定失败 {name}（PID {pid}）→ {label}：{result.Error}");
            }
        }

        /// <summary>
        /// 按进程名找规则，返回 null 表示无需绑定（无规则 / 被禁用 / 目标为 None）。
        ///
        /// 规则集带一个短窗口去抖：开机、启动大型软件时几十个进程会在同一两秒内
        /// 连续触发事件，若每次都 Reload（同步读盘 + JSON 反序列化）会把这点
        /// 「零开销」优势吃掉一半。1 秒内复用同一份规则足够 —— 用户保存规则后
        /// 秒级生效，与实时无差别。
        /// </summary>
        private static BindingRule? FindRuleFor(string name)
        {
            RefreshRulesIfStale();

            return BindingStore.Profile.Rules.FirstOrDefault(rule => Matches(rule, name));
        }

        private static bool Matches(BindingRule rule, string name) =>
            rule.Enabled &&
            rule.Target != BindingTarget.None &&
            !string.IsNullOrWhiteSpace(rule.Name) &&
            string.Equals(rule.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>规则去抖窗口。超过这个时长才重新读盘。</summary>
        private const int RuleReloadWindowMs = 1000;

        private static long _lastRuleReloadTick = long.MinValue / 2;

        private static void RefreshRulesIfStale()
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastRuleReloadTick);
            if (now - last < RuleReloadWindowMs)
                return;

            // 抢占式：只有把时间戳推进成功的那个线程去读盘，
            // 其余线程直接用即将就绪的新规则，避免同一瞬间多线程一起读同一个文件。
            if (Interlocked.CompareExchange(ref _lastRuleReloadTick, now, last) != last)
                return;

            BindingStore.Reload();
        }

        /// <summary>强制下一次查找重新读盘（保存规则后由 ResetCache 触发）。</summary>
        private static void InvalidateRuleCache()
        {
            Interlocked.Exchange(ref _lastRuleReloadTick, long.MinValue / 2);
        }

        // ==================== 启动时的全量补扫 ====================

        /// <summary>
        /// 事件模式启动时补一次全量扫描。
        /// WMI 事件只覆盖订阅之后发生的进程创建，启动前已在运行的程序
        /// （浏览器、游戏启动器、常驻服务）不会产生事件，必须主动过一遍。
        /// 这是事件模式下唯一的一次全量枚举，跑完即止。
        /// </summary>
        private static void InitialScan()
        {
            try
            {
                // 主动采一次，而不是等 Refresher —— 它现在只在有人看列表时才跑。
                IReadOnlyList<ProcessRefresher.Entry> snapshot = TakeSnapshotForScan();
                foreach (ProcessRefresher.Entry entry in snapshot)
                {
                    try
                    {
                        HandleProcess(entry.Pid, entry.Name);
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"启动补扫单个进程失败：{ex.Message}");
                    }
                }

                LogService.Info($"进程绑核启动补扫完成，共检查 {snapshot.Count} 个进程");
            }
            catch (Exception ex)
            {
                LogService.Warn($"进程绑核启动补扫失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 主动采一次进程快照。
        ///
        /// 不能像旧版那样「等 Refresher 自己采出第一份」——0.7.0 起 Refresher
        /// 只在有人看列表时才跑，启动时没人看，它会一直挂起，Latest 恒为空。
        /// 所以这里自己触发一次，用完即走，不改变它的挂起状态。
        /// </summary>
        private static IReadOnlyList<ProcessRefresher.Entry> TakeSnapshotForScan()
        {
            try
            {
                ProcessRefresher.Sample();
            }
            catch (Exception ex)
            {
                LogService.Warn($"启动补扫：进程快照失败：{ex.Message}");
            }

            return ProcessRefresher.Latest;
        }

        // ==================== 降级轮询主循环 ====================

        private static async System.Threading.Tasks.Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ScanOnce();
                }
                catch (Exception ex)
                {
                    LogService.Warn($"进程绑核扫描异常（已跳过本轮）：{ex.Message}");
                }

                try
                {
                    await System.Threading.Tasks.Task.Delay(FallbackIntervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 降级轮询：遍历最近一次进程快照，逐个交给 HandleProcess。
        /// 仅在 WMI 不可用时启用，因此不再单独重读规则 —— 规则读取已由 FindRuleFor 承担。
        /// </summary>
        private static void ScanOnce()
        {
            // 必须自己采一次：Refresher 只在用户看列表时才跑，
            // 否则 Latest 恒为上一份（或空），降级轮询会完全失效。
            IReadOnlyList<ProcessRefresher.Entry> processes = ProcessRefresher.Latest;
            if (processes.Count == 0)
                processes = TakeSnapshotForScan();

            // 本轮仍存活的 PID，用于清理淘汰项
            HashSet<int> alive = new();

            foreach (ProcessRefresher.Entry entry in processes)
            {
                alive.Add(entry.Pid);

                try
                {
                    HandleProcess(entry.Pid, entry.Name);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"绑定 {entry.Name}（PID {entry.Pid}）时异常：{ex.Message}");
                }
            }

            // 清理已退出进程的 PID（防止 PID 复用导致新进程被误判为已绑定）。
            // 用「本轮存活数」做上界判断：记录数不可能少于存活数，多出来的必然已退出。
            if (_handledPids.Count > alive.Count)
                _handledPids.PruneTo(alive);
        }

        // ==================== 辅助 ====================

        /// <summary>
        /// 线程安全的 PID 集合。所有操作在同一把锁内完成，调用方无需再关心同步。
        ///
        /// 组合操作（TryAdd 的「查+写」、PruneTo 的「比值+删除」）必须自己带锁，
        /// 拆成 Contains 再 Add 两步会在两步之间被其他线程插入。
        /// </summary>
        private sealed class PidSet
        {
            private readonly HashSet<int> _set = new();

            public int Count
            {
                get { lock (_set) return _set.Count; }
            }

            /// <summary>不存在则加入；返回 true 表示本次是新加入的（需要执行绑定）。</summary>
            public bool TryAdd(int pid)
            {
                lock (_set) return _set.Add(pid);
            }

            public bool Contains(int pid)
            {
                lock (_set) return _set.Contains(pid);
            }

            public void Remove(int pid)
            {
                lock (_set) _set.Remove(pid);
            }

            public void Clear()
            {
                lock (_set) _set.Clear();
            }

            /// <summary>只保留 <paramref name="alive"/> 中仍存在的 PID。</summary>
            public void PruneTo(HashSet<int> alive)
            {
                lock (_set) _set.RemoveWhere(pid => !alive.Contains(pid));
            }
        }
    }
}
