using System;
using System.Runtime.InteropServices;

namespace Zentwik.Services
{
    /// <summary>
    /// 唤醒宿主窗口：整个程序唯一的跨进程寻址点。
    ///
    /// 为什么不用命名内核对象：后台常驻实例可能由计划任务以最高权限拉起，
    /// 用户双击启动的是中等权限。低 → 高方向，EventWaitHandle.OpenExisting 会被 ACL 拒绝，
    /// 表现为「明明有一个在跑，双击却没反应，还多出一个幽灵进程」。
    /// 窗口消息只要对方放行了 UIPI，就能收到。
    ///
    /// 四个必须踩准的点（任一写错，唤醒就会「看起来成功、实际没反应」）：
    ///   1. 不能用 HWND_MESSAGE：message-only 窗口不参与 Z 序，跨完整性级别时
    ///      FindWindow 命中不稳定。必须是 0×0 的 WS_POPUP | WS_EX_TOOLWINDOW 顶层窗。
    ///   2. 消息号必须来自 RegisterWindowMessage：WM_APP + n 是进程内私有范围，
    ///      同一个数值在另一个进程里含义完全不同。
    ///   3. 必须 ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW)：UIPI 默认丢弃
    ///      低权限进程发往高权限窗口的自定义消息，且 PostMessage 照样返回 true。
    ///   4. 委托必须由静态字段持有：GetFunctionPointerForDelegate 之后委托一旦被回收，
    ///      窗口过程就是野指针，下一次收消息直接崩进程。
    /// </summary>
    internal static class WakeWindow
    {
        /// <summary>窗口类名。FindWindow 靠它定位，改了这个字符串等于改了对外契约。</summary>
        private const string ClassName = "Zentwik.WakeHost";

        /// <summary>窗口标题。仅用于调试观察（Spy++ 里能认出来），FindWindow 传 null 不限。</summary>
        private const string WindowTitle = "Zentwik";

        /// <summary>注册消息用的名字。系统级唯一，跨进程、跨会话都一致。</summary>
        private const string MessageName = "Zentwik.Activate";


        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>MSGFLT_ALLOW：把这条消息从 UIPI 过滤器里放行。</summary>
        private const uint MSGFLT_ALLOW = 1;

        private static IntPtr _hwnd;
        private static ushort _classAtom;
        private static uint _wakeMessage;

        /// <summary>必须由静态字段持有，否则 GC 回收后 WndProc 变野指针。</summary>
        private static WndProcDelegate? _wndProc;

        private static bool _disposed;

        /// <summary>
        /// 收到唤醒消息时做什么。由宿主注入：
        ///   · GUI 模式：Dispatcher.BeginInvoke(唤出主窗口)；
        ///   · 纯后台模式：RequestUpgrade（销毁托盘 → 加载 WPF）。
        ///
        /// 允许为 null：窗口可能建在宿主注入之前，此时只记日志。
        /// 这个委托在窗口线程上被调用（GUI 下是 UI 线程，后台下是消息循环线程）。
        /// </summary>
        public static Action? Activated { get; set; }

        /// <summary>唤醒消息号。`0` 表示尚未注册成功，此时 <see cref="Signal"/> 必定失败。</summary>
        public static uint WakeMessage => _wakeMessage;

        /// <summary>本进程是否已成功建出宿主窗口。</summary>
        public static bool IsCreated => _hwnd != IntPtr.Zero;


        /// <summary>
        /// 建出隐藏宿主窗（幂等）。
        ///
        /// 必须在 <see cref="SingleInstance.TryAcquire"/> 之后调用 —— 抢到锁的那个进程
        /// 才是唯一该被唤醒的对象，没抢到锁的进程建这个窗口只会误导 FindWindow。
        /// </summary>
        /// <returns>true = 窗口可用（含「本来就已经建好」）。</returns>
        public static bool EnsureCreated()
        {
            if (_hwnd != IntPtr.Zero)
                return true;

            if (_disposed)
                return false;

            try
            {
                // RegisterWindowMessage 是幂等的：同一个字符串重复注册返回同一个 ID，
                // 失败（返回 0）说明系统资源耗尽，没有别的补救办法。
                if (_wakeMessage == 0)
                {
                    _wakeMessage = NativeMethods.RegisterWindowMessage(MessageName);
                    if (_wakeMessage == 0)
                    {
                        Svc.Log.Warn("注册唤醒消息失败（RegisterWindowMessage 返回 0），唤醒退回命名事件");
                        return false;
                    }
                }

                IntPtr hInstance = NativeMethods.GetModuleHandle(null);

                _wndProc = WndProc;

                WNDCLASSEX wc = new()
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = ClassName,
                    hIconSm = IntPtr.Zero
                };

                // 类已注册（升级路径下另一个宿主建过同名类）会返回 0 + ERROR_CLASS_ALREADY_EXISTS，
                // 这时直接拿来用，不算失败。
                _classAtom = NativeMethods.RegisterClassEx(ref wc);
                if (_classAtom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                    if (err != ERROR_CLASS_ALREADY_EXISTS)
                    {
                        Svc.Log.Warn($"注册唤醒窗口类失败（Win32 错误 {err}），唤醒退回命名事件");
                        return false;
                    }
                }

                // 0×0 顶层窗：不在任务栏出现、不进 Alt+Tab、不抢焦点，
                // 但 FindWindow 能稳定命中，PostMessage 能稳定送达。
                _hwnd = NativeMethods.CreateWindowEx(
                    WS_EX_TOOLWINDOW,
                    ClassName,
                    WindowTitle,
                    WS_POPUP,
                    0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    Svc.Log.Warn($"创建唤醒宿主窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}），唤醒退回命名事件");
                    return false;
                }

                // 关键一步：把这条自定义消息加进本窗口的 UIPI 白名单。
                // 不放行的话，低权限实例发来的消息会被内核静默丢弃，而 PostMessage 照样返回 true。
                if (NativeMethods.ChangeWindowMessageFilterEx(_hwnd, _wakeMessage, MSGFLT_ALLOW, IntPtr.Zero))
                {
                    Svc.Log.Info($"唤醒宿主窗口已就绪（消息 0x{_wakeMessage:X4}，已放行 UIPI）");
                }
                else
                {
                    // 不走全进程 Add（会让所有窗口都放行），只记一条：
                    // 同权限级场景下唤醒依然可用，只是跨权限级会失效。
                    Svc.Log.Warn(
                        $"ChangeWindowMessageFilterEx 失败（Win32 错误 {Marshal.GetLastWin32Error()}）：" +
                        "跨权限级唤醒可能失效，命名事件兜底仍在");
                }

                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"创建唤醒宿主窗口异常：{ex.Message}");
                Destroy();
                return false;
            }
        }

        /// <summary>
        /// 请求已运行的那个实例唤出界面。
        ///
        /// 自己不可用（消息未注册 / 窗口不存在）时返回 false，调用方据此回退到命名事件。
        /// </summary>
        public static bool Signal()
        {
            if (_wakeMessage == 0)
                return false;

            IntPtr target = NativeMethods.FindWindow(ClassName, null);
            if (target == IntPtr.Zero)
                return false;

            // PostMessage 只是把消息塞进对方的队列，不等处理。
            // 返回值不代表对方收下了 —— UIPI 拦截时也返回 true，
            // 真正的保障是对方在 EnsureCreated 里做了 MSGFLT_ALLOW。
            return NativeMethods.PostMessage(target, _wakeMessage, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>销毁宿主窗口。进程退出前调用，幂等。</summary>
        public static void Destroy()
        {
            _disposed = true;

            if (_hwnd != IntPtr.Zero)
            {
                try { NativeMethods.DestroyWindow(_hwnd); }
                catch { /* 忽略 */ }

                _hwnd = IntPtr.Zero;
            }

            IntPtr hInstance = NativeMethods.GetModuleHandle(null);
            if (_classAtom != 0 && hInstance != IntPtr.Zero)
            {
                try { NativeMethods.UnregisterClass(ClassName, hInstance); }
                catch { /* 忽略 */ }

                _classAtom = 0;
            }

            // 消息号故意不清零：RegisterWindowMessage 的 ID 是系统级的，
            // 同一进程内再次注册会拿到同一个值，留着不影响任何判断。
        }


        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_wakeMessage != 0 && msg == _wakeMessage)
            {
                try
                {
                    Action? handler = Activated;
                    if (handler == null)
                    {
                        Svc.Log.Warn("收到唤醒消息，但宿主尚未注入处理器，已忽略");
                    }
                    else
                    {
                        handler();
                    }
                }
                catch (Exception ex)
                {
                    // 唤醒失败不该拖垮常驻实例
                    Svc.Log.Warn($"处理唤醒消息失败：{ex.Message}");
                }

                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }


        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr GetModuleHandle(string? lpModuleName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint RegisterWindowMessage(string lpString);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateWindowEx(
                uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
                int x, int y, int nWidth, int nHeight,
                IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool ChangeWindowMessageFilterEx(
                IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

            [DllImport("user32.dll")]
            internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        }
    }
}
