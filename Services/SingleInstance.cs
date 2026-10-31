using System;
using System.Threading;

namespace Zentwik.Services
{
    /// <summary>
    /// 单实例 + 唤醒已有实例。
    ///
    /// 为什么必须有：常驻之后用户双击图标会再开一个进程，两个实例去抢 ProcessWatcher 的
    /// 全局互斥体，抢不到的那个监听静默失效，界面开关却还显示「已开启」。
    /// 有了单实例，第二个进程只负责把常驻的那个唤醒，然后自己退出。
    ///
    /// 作用域用 Local\（会话级）不是 Global\：Global\ 需要 SeCreateGlobalPrivilege，
    /// 跨完整性级别时 OpenExisting 可能直接抛无权限异常，结果就是「明明有一个在跑
    /// 却又开了一个」。跨会话 / 跨用户只需一个实例在绑核，由 ProcessWatcher 自己的
    /// Global\ 互斥体保证。
    /// </summary>
    public static class SingleInstance
    {
        private const string MutexName = @"Local\Zentwik.SingleInstance";
        private const string WakeName = @"Local\Zentwik.SingleInstance.Wake";

        private static Mutex? _mutex;
        private static EventWaitHandle? _wake;

        /// <summary>
        /// 收到唤醒请求时的动作。由宿主注入：
        ///   · GUI 模式：回到 UI 线程唤出主窗口；
        ///   · 纯后台模式：向隐藏窗口投递 WM_APP_WAKE，由消息循环决定是否升级界面。
        ///
        /// 允许为 null：监听线程可能先于宿主初始化启动，此时只记日志，不抛异常。
        /// 这个属性取代了原先写死的 Application.Current?.Dispatcher 调用 —— 那是本类
        /// 唯一的 WPF 依赖，留着它，纯后台路径就会被拖进 PresentationFramework。
        /// </summary>
        public static Action? WakeRequested { get; set; }

        /// <summary>
        /// 只探测不占坑：已有实例就返回 true，并（可选）请求它把主窗口叫出来。
        ///
        /// 必须在提权之前调用。否则用户双击时明明可以直接唤起已有窗口，
        /// 却先弹一次 UAC 才退出 —— 白白打断一次。
        /// </summary>
        /// <param name="signalMain">
        /// 是否唤醒对方的主窗口。静默启动（-silent）要传 false：
        /// 用户敲 -silent 就是不想看见界面，不能因为已有实例在跑就弹出个窗口。
        /// </param>
        public static bool TrySignalExisting(bool signalMain)
        {
            // 对方可能刚建完互斥体、唤醒通道还没建出来。这时候直接判「没有别的实例」
            // 会让第二次双击毫无反应 —— 既不唤醒已有的，自己又会在 TryAcquire 撞墙退出。
            // 给一小段重试窗口把它等出来。
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    // 只确认「有人在」，不开句柄干等，拿到就放
                    using Mutex probe = Mutex.OpenExisting(MutexName);
                    if (!signalMain)
                        return true;
                }
                catch
                {
                    // 连互斥体都打不开 = 没有别的实例在跑，正常往下走
                    return false;
                }

                // 通道 1：窗口消息。跨权限级首选 —— 命名事件会被 ACL 挡住，
                // 窗口消息不会（对方在 EnsureCreated 里放行过 UIPI）。
                if (WakeWindow.Signal())
                    return true;

                // 通道 2：命名事件。窗口还没建好或注册消息失败时的兜底，指向同一个回调。
                try
                {
                    using EventWaitHandle wake = EventWaitHandle.OpenExisting(WakeName);
                    wake.Set();
                    return true;
                }
                catch
                {
                    Thread.Sleep(50);
                }
            }

            // 人在，但叫不醒。本实例退出即可，不至于双开。
            return true;
        }

        /// <summary>
        /// 正式占坑并起监听线程。返回 false 表示已经有别人占着了。
        /// </summary>
        public static bool TryAcquire()
        {
            try
            {
                // 事件必须先于互斥体建出来。
                // 顺序反了会出现这种空档：本进程刚建完互斥体、事件还没建，
                // 此时后启动的那个进程互斥体探测命中、事件探测扑空，
                // 于是既不唤醒本进程、自己又开不起来 —— 用户双击第二次完全没反应。
                _wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName, out _);

                _mutex = new Mutex(false, MutexName, out bool created);
                if (!created)
                {
                    Dispose();
                    return false;
                }

                Thread listener = new(ListenForWake)
                {
                    IsBackground = true,
                    Name = "Zentwik.Wake"
                };
                listener.Start();

                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Warn($"单实例互斥体创建失败，按单实例不可用处理：{ex.Message}");
                Dispose();

                // 拿不到锁也要让程序能用，绝不因为互斥体失败拒绝启动。
                // 最坏情况是双开，而不是打不开。
                return true;
            }
        }

        /// <summary>等到唤醒信号就到 UI 线程把主窗口叫出来。</summary>
        private static void ListenForWake()
        {
            EventWaitHandle? wake = _wake;
            while (true)
            {
                try
                {
                    if (wake == null)
                        return;

                    wake.WaitOne();
                }
                catch
                {
                    // Release() 把句柄 Dispose 掉了，线程该退了
                    return;
                }

                try
                {
                    // 每轮重新取：宿主可能在监听线程启动之后才注入处理器
                    Action? handler = WakeRequested;
                    if (handler == null)
                    {
                        Svc.Log.Warn("收到唤醒请求，但宿主尚未注入处理器，已忽略");
                        continue;
                    }

                    handler();
                }
                catch (Exception ex)
                {
                    // 唤醒失败不该拖垮常驻实例
                    Svc.Log.Warn($"唤醒处理失败：{ex.Message}");
                }
            }
        }

        /// <summary>释放互斥体与事件，供进程退出前调用。</summary>
        public static void Release()
        {
            Dispose();
        }

        private static void Dispose()
        {
            try
            {
                _wake?.Dispose();
            }
            catch
            {
                // 忽略
            }

            try
            {
                _mutex?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _wake = null;
            _mutex = null;
        }
    }
}
