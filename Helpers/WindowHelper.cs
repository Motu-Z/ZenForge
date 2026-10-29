using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 窗口外观辅助：Windows 11 圆角 + 深色标题栏（DWM 属性，失败时静默忽略）。
    /// </summary>
    internal static class WindowHelper
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        /// <summary>在 SourceInitialized 之后调用：为 Win11 开启圆角窗口。</summary>
        public static void ApplyWindowAttributes(Window window)
        {
            try
            {
                // 这里必须 EnsureHandle：SourceInitialized 阶段句柄可能还没建出来，
                // 而下面三个方法只作查询，拿不到就放弃。
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    int round = DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                }
            }
            catch
            {
                // 旧系统保持默认直角
            }
        }

        /// <summary>
        /// 打上 WS_EX_TOOLWINDOW：窗口不进 Alt+Tab、不进任务栏。
        /// 托盘菜单需要一个「已显示但看不见」的宿主窗口（WPF 的 ContextMenu
        /// 必须挂在一个真实窗口上才能正常捕获鼠标、点击别处自动收起），
        /// 不打这个扩展样式它就会出现在 Alt+Tab 里，变成一个 1×1 的空白条目。
        /// </summary>
        public static void MarkAsToolWindow(Window window)
        {
            try
            {
                if (!TryGetHandle(window, out IntPtr hwnd))
                    return;

                IntPtr style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style.ToInt64() | WS_EX_TOOLWINDOW));

                // 只改样式不够：必须再发一次 SWP_FRAMECHANGED，让外壳重新读取
                // 扩展样式，否则 Alt+Tab 列表不会刷新，那个 1×1 空条目照样在。
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE);
            }
            catch
            {
                // 失败只是 Alt+Tab 多一条，不影响功能
            }
        }

        /// <summary>
        /// 把窗口提到前台。
        ///
        /// Activate() 在「进程不是当前前台进程」时经常失效 —— 典型场景就是
        /// 从托盘图标唤醒：用户点的是任务栏，前台进程其实是 explorer.exe，
        /// WPF 不允许后台进程自己抢占前台，Activate 只让任务栏图标闪。
        /// 这里直接调 SetForegroundWindow 补上。
        /// </summary>
        public static void ForceForeground(Window window)
        {
            try
            {
                if (!TryGetHandle(window, out IntPtr hwnd))
                    return;

                SetForegroundWindow(hwnd);
            }
            catch
            {
                // 抢不到前台就保持原状，用户再点一次即可
            }
        }

        /// <summary>标题栏深/浅色跟随应用主题。</summary>
        public static void ApplyTitleBarTheme(Window window, bool dark)
        {
            try
            {
                if (!TryGetHandle(window, out IntPtr hwnd))
                    return;

                int value = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            }
            catch
            {
                // 忽略：不支持的系统保持默认
            }
        }

        private static bool TryGetHandle(Window window, out IntPtr hwnd)
        {
            hwnd = new WindowInteropHelper(window).Handle;
            return hwnd != IntPtr.Zero;
        }
    }
}
