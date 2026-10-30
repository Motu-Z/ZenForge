using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace ZenForge.Services
{
    /// <summary>
    /// 供电状态监听（电池 / 交流）。
    ///
    /// 【为什么自己包一层，而不是让 ScenarioService 直接读 SystemInformation.PowerStatus】
    /// 供电状态是**场景自动切换的两个电源类触发源**的输入。读一次状态只要一次系统调用，
    /// 但「状态什么时候变了」需要有人盯着 —— 而这个「盯着」的生命周期（什么时候开始、
    /// 什么时候停、停了要不要留兜底）不该混进判定逻辑里。本类只负责「报告状态变了」，
    /// 判定归 ScenarioService。
    ///
    /// 【为什么不用 System.Windows.Forms.SystemInformation】
    /// 那要引 System.Windows.Forms，为一个字段把整个 WinForms 拖进一个纯 WPF 工程
    /// 不值得 —— 它背后也只是 GetSystemPowerStatus，这里直接调同一个 API。
    /// </summary>
    public static class PowerStatusWatcher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        private const byte AcLineOffline = 0;
        private const byte AcLineOnline = 1;

        /// <summary>
        /// 兜底轮询间隔。
        ///
        /// 【为什么还需要轮询】
        /// SystemEvents.PowerModeChanged 依赖消息泵：WPF 主窗口在时它有，
        /// 但静默模式（-silent，无窗口）下不一定有。更麻烦的是它本来就漏事件 ——
        /// 部分 OEM 的电源管理不广播 StatusChange，插拔电源只有 WMI/轮询能看到。
        /// 一次 GetSystemPowerStatus 是一次系统调用，不枚举任何东西，
        /// 30 秒一次的开销与「后台零占用」的约束不冲突（那条约束针对的是
        /// 全量遍历进程这类百毫秒级的操作）。
        ///
        /// 30 秒是「用户拔了电源走到桌前发现方案没切」的可感知上限附近，
        /// 再短就是为没人会注意的时间差付轮询成本。
        /// </summary>
        private const int PollIntervalMs = 30_000;

        /// <summary>供电状态变化。</summary>
        public static event Action? Changed;

        private static Timer? _poll;
        private static int _attached;
        private static int _lastKnownAc = -1;

        /// <summary>当前是否电池供电。台式机（无电池）恒为 false。</summary>
        public static bool IsOnBattery
        {
            get
            {
                if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
                    return false;

                // 255 = Unknown。读不到就当插着电：OnBattery 规则不命中，
                // 代价是「拔了电源没切方案」；反过来猜成电池则会误切，
                // 用户看到的是「我没拔电源怎么自己变了」—— 后者更难解释。
                return status.ACLineStatus == AcLineOffline;
            }
        }

        /// <summary>
        /// 开始监听。幂等：重复调用只挂一次。
        /// </summary>
        public static void Start()
        {
            if (Interlocked.Exchange(ref _attached, 1) != 0)
                return;

            _lastKnownAc = IsOnBattery ? 0 : 1;

            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                // SystemEvents 需要一个消息泵。挂不上不致命 —— 下面还有轮询兜底。
                LogService.Warn($"电源事件订阅失败，改用轮询检测：{ex.Message}");
            }

            // dueTime 给足一个完整间隔：启动瞬间刚读过一次状态，不需要立刻再读。
            _poll = new Timer(OnPollTick, null, PollIntervalMs, PollIntervalMs);
        }

        /// <summary>
        /// 停止监听。幂等。
        ///
        /// 必须真的把 Timer 释放掉：场景总闸关掉后如果它还每 30 秒醒一次，
        /// 「关掉就等于零占用」这条约定就是假的。
        /// </summary>
        public static void Stop()
        {
            if (Interlocked.Exchange(ref _attached, 0) != 0)
                return;

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch
            {
                // 卸载失败无需处理：进程退出时系统自行回收
            }

            Timer? timer = Interlocked.Exchange(ref _poll, null);
            timer?.Dispose();
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // 只关心供电状态变化。Suspend / Resume 是睡眠与恢复，
            // 那时供电状态未必变（合盖再打开，还是插着电），
            // 跟着重评一遍场景等于白切一次方案。
            if (e.Mode != PowerModes.StatusChange)
                return;

            RaiseIfChanged();
        }

        private static void OnPollTick(object? state)
        {
            try
            {
                RaiseIfChanged();
            }
            catch (Exception ex)
            {
                LogService.Warn($"电源状态轮询异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 只在状态真的翻转时才广播。
        ///
        /// 轮询每 30 秒一次，无条件广播会让订阅方跟着每 30 秒重评一遍场景；
        /// 而重评要取一次全量进程名单 —— 那是几百次 OpenProcess，比这次轮询贵得多。
        /// </summary>
        private static void RaiseIfChanged()
        {
            int current = IsOnBattery ? 0 : 1;
            if (Interlocked.Exchange(ref _lastKnownAc, current) == current)
                return;

            LogService.Info(current == 0 ? "供电状态：电池" : "供电状态：交流");

            Action? handler = Changed;
            if (handler == null)
                return;

            try { handler(); }
            catch (Exception ex) { LogService.Warn($"电源变化订阅者处理失败：{ex.Message}"); }
        }
    }
}
