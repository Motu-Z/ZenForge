using System;
using System.Collections.Generic;
using System.Threading;

namespace ZenForge.Services
{
    /// <summary>
    /// 场景自动切换的事件源编排（v1.1.x 实装）。
    ///
    /// 【它站在哪一层】
    /// ScenarioService 是纯判定：给它「此刻哪些场景成立」，它回答「该切哪套方案」。
    /// 本类负责回答前半句 —— 谁在跑、插没插电、什么时候该重新问一次。
    /// 两层分开的原因和别处一样：判定逻辑要能被单独验证（甚至被界面拿去做预览），
    /// 而「怎么知道世界变了」牵扯线程、防抖、启停，混在一起就没法单独测了。
    ///
    /// 【三条硬约束】
    ///   1. **不启用就零占用**：场景总闸关着、或一条规则都没配时，不订阅任何事件、
    ///      不持有任何活动定时器。这不是洁癖 —— 本工程对后台的要求是
    ///      「没人看的时候占用必须归零」，而这个功能天然是常驻的。
    ///   2. **按需订阅**：只有进程类规则才挂进程事件，只有电源类规则才挂电源监听。
    ///      用户只配了一条「插电」规则时，为一个用不上的输入付采集代价没有道理。
    ///   3. **防抖在调用侧**：游戏启动会瞬间拉起十几个子进程，逐个通知就是十几次
    ///      全量评估（每次都要采一遍进程名单）。这里合并成一个窗口内的最后一次。
    ///      按 ScenarioService.Notify 的约定，防抖不放进服务内部 ——
    ///      延时取多长只有调用方知道，服务里拍一个数字没有依据。
    /// </summary>
    public static class ScenarioMonitor
    {
        /// <summary>
        /// 合并窗口。
        ///
        /// 800ms 的依据是「游戏启动拉起子进程的典型跨度」：主进程起来后，反作弊、
        /// 启动器、浏览器内核等子进程在几百毫秒内陆续出现。短于这个窗口会拆成
        /// 好几次评估（每次都白采一遍名单），长于它则用户能感觉到方案切晚了。
        /// </summary>
        private const int DebounceMs = 800;

        /// <summary>采一次进程名单最多等这么久（采样线程忙时不能无限等）。</summary>
        private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);

        private static readonly object Gate = new();
        private static Timer? _debounce;
        private static bool _processHooked;
        private static bool _powerHooked;
        private static bool _active;

        static ScenarioMonitor()
        {
            // 规则或总闸一变，监听范围就要重算：从「只听电源」变成「也要听进程」，
            // 或从「开着」变成「关掉」。挂一次就够，静态类的生命周期等于进程。
            ScenarioService.Changed += Sync;
        }

        /// <summary>当前是否在监听任何输入源。false 等于「零占用」（无订阅、无活动定时器）。</summary>
        public static bool IsActive
        {
            get { lock (Gate) return _active; }
        }

        /// <summary>进程事件是否已挂上。</summary>
        public static bool IsProcessSourceAttached
        {
            get { lock (Gate) return _processHooked; }
        }

        /// <summary>电源监听是否已挂上。</summary>
        public static bool IsPowerSourceAttached => _powerHooked;

        /// <summary>
        /// 按当前规则内容对齐监听范围。幂等，任何时刻调用都安全。
        /// 启动时、规则变更后都要调一次。
        /// </summary>
        public static void Sync()
        {
            bool enabled = ScenarioService.Enabled;
            bool wantProcess = enabled && ScenarioService.HasProcessRules;
            bool wantPower = enabled && ScenarioService.HasPowerRules;

            lock (Gate)
            {
                if (wantProcess && !_processHooked)
                {
                    ProcessWatcher.ProcessSetChanged += OnProcessSetChanged;
                    _processHooked = true;
                }
                else if (!wantProcess && _processHooked)
                {
                    ProcessWatcher.ProcessSetChanged -= OnProcessSetChanged;
                    _processHooked = false;
                }
            }

            // 电源监听自己管启停与幂等（内部有 Interlocked 闸门 + 轮询兜底）。
            if (wantPower && !_powerHooked)
            {
                PowerStatusWatcher.Changed += OnPowerChanged;
                PowerStatusWatcher.Start();
                _powerHooked = true;
            }
            else if (!wantPower && _powerHooked)
            {
                PowerStatusWatcher.Changed -= OnPowerChanged;
                PowerStatusWatcher.Stop();
                _powerHooked = false;
            }

            bool wasActive;
            lock (Gate)
            {
                wasActive = _active;
                _active = wantProcess || wantPower;
            }

            if (!_active)
            {
                DisposeDebounce();
                return;
            }

            // 刚从「没在监听」变成「在监听」时立刻评估一次：
            // 此刻可能已经有进程在跑、已经插着电，等下一个事件才切就是漏掉了当前状态。
            if (!wasActive)
                Schedule();
        }

        /// <summary>合并窗口内到达的事件只保留最后一次。</summary>
        private static void Schedule()
        {
            lock (Gate)
            {
                if (!_active)
                    return;

                // 定时器按需创建、period 设为 Infinite：只在被 Schedule 推一把时醒一次，
                // 静默期它不产生任何唤醒 —— 这是「零占用」这条约束的具体兑现方式。
                _debounce ??= new Timer(_ => Evaluate(), null, Timeout.Infinite, Timeout.Infinite);
                _debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }

        private static void OnProcessSetChanged() => Schedule();

        private static void OnPowerChanged() => Schedule();

        private static void DisposeDebounce()
        {
            Timer? timer;
            lock (Gate)
                timer = Interlocked.Exchange(ref _debounce, null);

            timer?.Dispose();
        }

        /// <summary>
        /// 采一次当前状态，交给 ScenarioService 判定。
        ///
        /// 【为什么传全量而不是增量】
        /// ScenarioService.Notify 的契约就是要「此刻全部成立的触发」：
        /// 只报「游戏退出了」会被读成「没有进程命中了」，而浏览器可能还在跑。
        /// </summary>
        private static void Evaluate()
        {
            if (!ScenarioService.Enabled)
                return;

            List<(ScenarioTrigger Trigger, string Argument)> active = new();

            if (ScenarioService.HasPowerRules)
            {
                // 供电是二选一，两者都上报反而会让「插着电」和「用电池」同时命中。
                active.Add(PowerStatusWatcher.IsOnBattery
                    ? (ScenarioTrigger.OnBattery, string.Empty)
                    : (ScenarioTrigger.OnAcPower, string.Empty));
            }

            if (ScenarioService.HasProcessRules)
            {
                IReadOnlyList<ProcessRefresher.Entry> snapshot;
                try
                {
                    snapshot = ProcessRefresher.SampleOnSamplerThread(SnapshotTimeout);
                }
                catch (Exception ex)
                {
                    // 采不到名单就不评估：带着一份残缺的名单去判定，
                // 结果可能是「唯一命中的那条规则恰好没被采到」→ 误切成别的方案。
                    LogService.Warn($"场景评估：进程名单采集失败，本次跳过：{ex.Message}");
                    return;
                }

                // 同名进程（chrome、svchost 动辄十几个）去重后再上报，
                // 否则规则匹配要对同一条规则重复比对几十次。
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                foreach (ProcessRefresher.Entry entry in snapshot)
                {
                    if (!string.IsNullOrEmpty(entry.Name) && seen.Add(entry.Name))
                        active.Add((ScenarioTrigger.ProcessRunning, entry.Name));
                }
            }

            ScenarioService.Notify(active);
        }
    }
}
