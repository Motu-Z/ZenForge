using System;
using System.Runtime.InteropServices;

namespace ZenForge.Services
{
    /// <summary>
    /// 纯 Win32 托盘图标 —— 不依赖 System.Windows.Forms，也不依赖 WPF。
    ///
    /// 为什么不用现成的 <see cref="TrayIconService"/>：那个类整个建立在
    /// NotifyIcon + WPF ContextMenu 上，只要碰一下，System.Windows.Forms 与
    /// PresentationFramework 就全进进程，后台常驻的轻量前提当场失效。
    ///
    /// 四个必须踩准的坑（写错了图标会「看起来正常、实际不可用」）：
    ///   1. 不能用 HWND_MESSAGE。TaskbarCreated 走 HWND_BROADCAST，
    ///      message-only 窗口收不到 —— 资源管理器重启后图标永久消失。
    ///      必须用 0×0 的 WS_POPUP | WS_EX_TOOLWINDOW 隐藏顶层窗。
    ///   2. 图标资源 ID 是 32512（ApplicationIcon 被编成 Win32 图标组）。
    ///   3. 菜单必须先 SetForegroundWindow，再 TrackPopupMenuEx，
    ///      最后 PostMessage(WM_NULL)，否则点窗口别处菜单不消失。
    ///   4. NIM_SETVERSION + NOTIFYICON_VERSION_4：v4 下回调的 LOWORD 是
    ///      WM_CONTEXTMENU / NIN_SELECT，不是旧版的 WM_RBUTTONUP / WM_LBUTTONDBLCLK。
    /// </summary>
    internal sealed class NativeTrayIcon : IDisposable
    {
        // ==================== 常量 ====================

        private const string WindowClassName = "ZenForge.HeadlessTray";

        private const uint WM_APP = 0x8000;
        private const uint WM_APP_WAKE = WM_APP + 1;
        private const uint WM_TRAYICON = WM_APP + 2;

        private const uint WM_NULL = 0x0000;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint NIN_SELECT = 0x0400;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIM_SETVERSION = 0x00000004;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const uint IMAGE_ICON = 1;
        private const uint LR_SHARED = 0x00008000;

        private const int IDI_APPLICATION = 32512;
        private const uint ApplicationIconResourceId = 32512;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;

        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY = 0x0080;

        private const int CommandOpen = 1;
        private const int CommandToggleWatch = 2;
        private const int CommandExit = 3;

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private const uint TrayIconId = 1;

        // ==================== 状态 ====================

        private readonly IntPtr _hInstance;
        private readonly WndProcDelegate _wndProc;   // 必须用字段持有，否则 GC 回收后回调崩溃

        private IntPtr _hwnd;
        private IntPtr _icon;
        private ushort _classAtom;
        private uint _taskbarCreatedMessage;
        private bool _added;
        private bool _disposed;

        private bool _upgradeRequested;
        private bool _exitRequested;

        /// <summary>用户要求打开界面（托盘单击 / 菜单「打开界面」）。</summary>
        public bool UpgradeRequested => _upgradeRequested;

        /// <summary>用户要求退出（菜单「退出」）。</summary>
        public bool ExitRequested => _exitRequested;

        public NativeTrayIcon(bool showIcon)
        {
            _hInstance = NativeMethods.GetModuleHandle(null);
            _wndProc = WndProc;

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            WNDCLASSEX wc = new()
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = _hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero
            };

            _classAtom = NativeMethods.RegisterClassEx(ref wc);
            if (_classAtom == 0)
                throw new InvalidOperationException(
                    $"注册托盘窗口类失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

            // 0×0 隐藏顶层窗：既不在任务栏出现，也能收到 HWND_BROADCAST 的 TaskbarCreated。
            //
            // 刻意不加 WS_EX_NOACTIVATE：它会阻止 SetForegroundWindow，
            // 而菜单必须先把窗口置前台，否则点击别处时菜单会赖在屏幕上不消失。
            // 不可见 + WS_EX_TOOLWINDOW 已经足够保证它不抢用户的焦点和任务栏位置。
            _hwnd = NativeMethods.CreateWindowEx(
                WS_EX_TOOLWINDOW,
                WindowClassName,
                "ZenForge",
                WS_POPUP,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"创建托盘消息窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

            if (showIcon)
            {
                _icon = LoadAppIcon();
                AddIcon();
            }
        }

        /// <summary>
        /// 供 <see cref="SingleInstance.WakeRequested"/> 回调：向隐藏窗口投递唤醒消息。
        /// 窗口已销毁（升级过程中）时安全返回。
        /// </summary>
        public void PostWake()
        {
            if (_hwnd != IntPtr.Zero)
                NativeMethods.PostMessage(_hwnd, WM_APP_WAKE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// 跑消息循环，直到收到唤醒（要升级界面）或退出请求。
        /// 必须在 STA 主线程上调用。
        /// </summary>
        public void RunMessageLoop()
        {
            while (NativeMethods.GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }

        // ==================== 窗口过程 ====================

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 资源管理器重启：所有托盘图标都被清空，必须重新加回去
            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                _added = false;
                AddIcon();
                return IntPtr.Zero;
            }

            if (msg == WM_TRAYICON)
            {
                // v4：LOWORD(lParam) 是事件类型
                uint evt = (uint)(lParam.ToInt64() & 0xFFFF);

                if (evt == WM_CONTEXTMENU || evt == WM_LBUTTONUP)
                {
                    ShowMenu();
                }
                else if (evt == NIN_SELECT || evt == WM_LBUTTONDBLCLK)
                {
                    RequestUpgrade();
                }

                return IntPtr.Zero;
            }

            if (msg == WM_APP_WAKE)
            {
                RequestUpgrade();
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void RequestUpgrade()
        {
            _upgradeRequested = true;
            NativeMethods.PostQuitMessage(0);
        }

        // ==================== 托盘图标 ====================

        /// <summary>
        /// 托盘通知结构的基础字段。只填定位信息（尺寸 / 窗口 / 图标 ID），
        /// 具体要操作哪几项由调用方按 NIM_ADD / NIM_DELETE 各自设置。
        /// </summary>
        private NOTIFYICONDATA NewIconData() => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        private void AddIcon()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            if (_icon == IntPtr.Zero)
            {
                // 消息窗口仍在工作，双击 exe 依然能唤起界面，只是任务栏没有图标
                LogService.Warn("托盘图标资源加载失败，任务栏不显示图标（后台仍在运行）");
                return;
            }

            NOTIFYICONDATA data = NewIconData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYICON;
            data.hIcon = _icon;
            data.szTip = "ZenForge — 后台绑核运行中";

            _added = NativeMethods.Shell_NotifyIconW(NIM_ADD, ref data);

            if (_added)
            {
                data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                NativeMethods.Shell_NotifyIconW(NIM_SETVERSION, ref data);
            }
        }

        private void ShowMenu()
        {
            IntPtr menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            try
            {
                NativeMethods.AppendMenuW(menu, MF_STRING, (IntPtr)CommandOpen, "打开界面");

                NativeMethods.AppendMenuW(
                    menu, MF_STRING, (IntPtr)CommandToggleWatch,
                    ProcessWatcher.IsRunning ? "暂停绑核" : "恢复绑核");

                NativeMethods.AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
                NativeMethods.AppendMenuW(menu, MF_STRING, (IntPtr)CommandExit, "退出");

                // 不先置前台，菜单会在点击别处时留在屏幕上不消失
                NativeMethods.SetForegroundWindow(_hwnd);

                NativeMethods.GetCursorPos(out POINT pt);

                int command = NativeMethods.TrackPopupMenuEx(
                    menu,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                    pt.X, pt.Y, _hwnd, IntPtr.Zero);

                // MSDN 明确要求：TrackPopupMenu 之后给窗口投一条空消息，
                // 否则菜单的残留状态会让下一次点击行为异常。
                NativeMethods.PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                switch (command)
                {
                    case CommandOpen:
                        RequestUpgrade();
                        break;

                    case CommandToggleWatch:
                        ToggleWatch();
                        break;

                    case CommandExit:
                        _exitRequested = true;
                        NativeMethods.PostQuitMessage(0);
                        break;
                }
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        private static void ToggleWatch()
        {
            try
            {
                if (ProcessWatcher.IsRunning)
                    ProcessWatcher.Stop();
                else
                    ProcessWatcher.Start();
            }
            catch (Exception ex)
            {
                LogService.Warn($"托盘切换绑核失败：{ex.Message}");
            }
        }

        private IntPtr LoadAppIcon()
        {
            // ApplicationIcon 编成 Win32 图标组，资源 ID 固定为 32512。
            // <Resource> 那一份在 g.resources 里，原生 API 取不到，只能用这个。
            IntPtr icon = NativeMethods.LoadImage(
                _hInstance, (IntPtr)ApplicationIconResourceId, IMAGE_ICON, 0, 0, LR_SHARED);

            if (icon != IntPtr.Zero)
                return icon;

            // 兜底：系统默认应用图标。LR_SHARED 的图标不要 DestroyIcon。
            return NativeMethods.LoadImage(
                IntPtr.Zero, (IntPtr)IDI_APPLICATION, IMAGE_ICON, 0, 0, LR_SHARED);
        }

        // ==================== 清理 ====================

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_added && _hwnd != IntPtr.Zero)
            {
                NOTIFYICONDATA data = NewIconData();
                try { NativeMethods.Shell_NotifyIconW(NIM_DELETE, ref data); }
                catch { /* 进程正在退出，删不掉也无所谓 */ }

                _added = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                try { NativeMethods.DestroyWindow(_hwnd); }
                catch { /* 忽略 */ }

                _hwnd = IntPtr.Zero;
            }

            if (_classAtom != 0)
            {
                try { NativeMethods.UnregisterClass(WindowClassName, _hInstance); }
                catch { /* 忽略 */ }

                _classAtom = 0;
            }
        }

        // ==================== 互操作 ====================

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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public uint dwState;
            public uint dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public uint uTimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
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
            internal static extern IntPtr CreateWindowEx(
                uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
                int x, int y, int nWidth, int nHeight,
                IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint RegisterWindowMessage(string lpString);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr LoadImage(
                IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr CreatePopupMenu();

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern int TrackPopupMenuEx(
                IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool DestroyMenu(IntPtr hMenu);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            internal static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

            [DllImport("user32.dll")]
            internal static extern bool TranslateMessage(ref MSG lpMsg);

            [DllImport("user32.dll")]
            internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

            [DllImport("user32.dll")]
            internal static extern void PostQuitMessage(int nExitCode);
        }
    }
}
