using System;
using System.Runtime.InteropServices;

namespace Zentwik.Services
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

        private const string WindowClassName = "Zentwik.HeadlessTray";

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

        private const uint PM_REMOVE = 0x0001;
        private const uint WM_QUIT = 0x0012;

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
                "Zentwik",
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
            MSG msg;

            // 不能用 GetMessage 收尾：PostQuitMessage 会往队列里塞一条 WM_QUIT，
            // GetMessage 取到它只返回 0、不清队列，于是上一轮的 WM_QUIT 一直躺着，
            // 下一轮循环刚起来就立刻退出。托盘建好后第一次唤醒界面走 WakeWindow、
            // 不碰这个队列还好，一旦走的是命名事件那条路，图标就「点一下就没了」。
            // PeekMessage 每轮都把 WM_QUIT 真正摘掉，循环条件才回到 `_exitRequested`。
            while (!_exitRequested && NativeMethods.PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                    break;

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
        /// 把 <see cref="NOTIFYICONDATA"/> 交给 <c>Shell_NotifyIconW</c>。
        ///
        /// 结构体用显式布局是为了让每个字段落在 C 编译器排出的位置，但显式布局下
        /// CLR 仍会按自己的规则把整块补齐到 976：末尾的 <c>hBalloonIcon</c> 之后
        /// 多出 8 字节填充，而 shell 只按 <c>cbSize</c>（968）读。这里把 cbSize
        /// 与声明写成一致，就不再依赖「多出来的没人读」这个前提。
        /// </summary>
        private static bool ShellNotifyIcon(uint message, in NOTIFYICONDATA data)
        {
            // 分配时按封送器实际要写的长度来，交出去时才截到 size。
            // 两者在 Vista 之前不一样，混用会把 hBalloonIcon 写到块外。
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NOTIFYICONDATA>());

            try
            {
                Marshal.StructureToPtr(data, buffer, false);

                // 结构体已按 C 布局排好，只需把封送器额外补齐出来的尾部砍掉：
                // cbSize 写多少，shell 就只读多少。
                Marshal.WriteInt32(buffer, 0, IconDataSize());
                return NativeMethods.Shell_NotifyIconW(message, buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// <c>cbSize</c> 取当前 shell 能接受的最大长度。
        ///
        /// 官方要求按 Shell32 版本给值：Vista 及以后是 <c>sizeof(NOTIFYICONDATA)</c>，
        /// XP 是 V3_SIZE。两者的差别只有末尾的 <c>hBalloonIcon</c> 一个指针，
        /// 本机最低 Win10，<c>Major >= 6</c> 之后恒走全长分支；保留另一支是为了
        /// 万一有人把这套代码搬到老系统上时不至于传错长度。
        /// </summary>
        private static int IconDataSize() =>
            Environment.OSVersion.Version.Major >= 6
                ? NOTIFYICONDATA.ExpectedSize
                : NOTIFYICONDATA.HBalloonIconOffset;

        /// <summary>
        /// 布局一旦和 shellapi.h 漂移，托盘会「看着正常、读到的字段却是别人的」。
        /// 显式布局的意义就是把偏移钉死，所以这里逐字段对拍，而不是指望运行时巧合。
        /// </summary>
        static NativeTrayIcon() => NOTIFYICONDATA.VerifyLayout();


        /// <summary>
        /// 托盘通知结构的基础字段。只填定位信息（尺寸 / 窗口 / 图标 ID），
        /// 具体要操作哪几项由调用方按 NIM_ADD / NIM_DELETE 各自设置。
        /// </summary>
        private NOTIFYICONDATA NewIconData() => new()
        {
            cbSize = IconDataSize(),
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
                Svc.Log.Warn("托盘图标资源加载失败，任务栏不显示图标（后台仍在运行）");
                return;
            }

            NOTIFYICONDATA data = NewIconData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYICON;
            data.hIcon = _icon;
            data.szTip = "Zentwik — 后台绑核运行中";

            _added = ShellNotifyIcon(NIM_ADD, data);

            if (_added)
            {
                data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                ShellNotifyIcon(NIM_SETVERSION, data);
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
                    Svc.Watcher.IsRunning ? "暂停绑核" : "恢复绑核");

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
                if (Svc.Watcher.IsRunning)
                    Svc.Watcher.Stop();
                else
                    Svc.Watcher.Start();
            }
            catch (Exception ex)
            {
                Svc.Log.Warn($"托盘切换绑核失败：{ex.Message}");
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
                try { ShellNotifyIcon(NIM_DELETE, NewIconData()); }
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

        /// <summary>
        /// <c>NOTIFYICONDATAW</c>，x64 布局，共 968 字节。
        ///
        /// 这里必须用显式布局。默认的 Sequential 排不出正确结果：托管封送器认为
        /// <c>Guid</c> 的对齐是 4 而不是 8，于是 <c>szInfoTitle</c> 之后那几个字段
        /// 整体前移 4 字节（<c>szInfoTitle</c> 落在 820 而 C 是 824），结构体还会
        /// 多撑到 976。cbSize 报 976 时 shell 按它自己的 968 布局去读，表面上能用，
        /// 读到的标题与状态位却是错的。<c>Pack = 1</c> 同样不行 —— 它会连 <c>hWnd</c>
        /// 的对齐一起压掉，从第二个字段就开始错位。
        ///
        /// 偏移取自 shellapi.h 在 x64 下由 C 编译器排出的实际结果。只支持 x64：
        /// 本项目只发布 win-x64，x86 下这些偏移不成立，<see cref="VerifyLayout"/> 会拦下。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 968, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            /// <summary>与 Win10+ shell 完全一致的整块长度，即 C 的 <c>sizeof</c>。</summary>
            public const int ExpectedSize = 968;

            /// <summary>V3_SIZE：<c>hBalloonIcon</c> 之前的长度，Vista 之前的结构到此为止。</summary>
            public const int HBalloonIconOffset = 968;

            [FieldOffset(0)]
            public int cbSize;

            [FieldOffset(8)]
            public IntPtr hWnd;

            [FieldOffset(16)]
            public uint uID;

            [FieldOffset(20)]
            public uint uFlags;

            [FieldOffset(24)]
            public uint uCallbackMessage;

            [FieldOffset(32)]
            public IntPtr hIcon;

            // Win2000 起为 WCHAR[128]，含结尾空字符，即最多 127 个可见字符。
            [FieldOffset(40)]
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            [FieldOffset(296)]
            public uint dwState;

            [FieldOffset(300)]
            public uint dwStateMask;

            // WCHAR[256]，与 uTimeoutOrVersion 共同构成 V2 的尾部。
            [FieldOffset(304)]
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            // 与 uTimeout 是联合体：NIM_SETVERSION 写这里。
            [FieldOffset(816)]
            public uint uTimeoutOrVersion;

            [FieldOffset(824)]
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            [FieldOffset(952)]
            public uint dwInfoFlags;

            // Win7 起新增。guidItem 是 16 字节，hBalloonIcon 紧随其后。
            [FieldOffset(960)]
            public Guid guidItem;

            // 到 V3_SIZE 为止。只有这一项在 Vista 之前不存在，届时 cbSize 报 960。
            [FieldOffset(968)]
            public IntPtr hBalloonIcon;

            /// <summary>
            /// 逐字段对拍 C 布局。显式布局只在 x64 下成立，x86 会在这里被拦下，
            /// 而不是等到托盘读错字段再派人去猜。
            ///
            /// 注意不能拿 <c>Marshal.SizeOf</c> 来校验：显式布局下 CLR 会把整块
            /// 补齐到 976（`Guid` 尾部多 8 字节），那个数字本来就不等于 <c>sizeof</c>。
            /// 真正决定字段是否读对的，是这些偏移。
            /// </summary>
            public static void VerifyLayout()
            {
                if (!Environment.Is64BitProcess)
                    throw new PlatformNotSupportedException("NOTIFYICONDATA 的偏移按 x64 计算，不支持 32 位进程");

                (string Field, int Offset)[] expected =
                {
                    (nameof(cbSize), 0), (nameof(hWnd), 8), (nameof(uID), 16), (nameof(uFlags), 20),
                    (nameof(uCallbackMessage), 24), (nameof(hIcon), 32), (nameof(szTip), 40),
                    (nameof(dwState), 296), (nameof(dwStateMask), 300), (nameof(szInfo), 304),
                    (nameof(uTimeoutOrVersion), 816), (nameof(szInfoTitle), 824),
                    (nameof(dwInfoFlags), 952), (nameof(guidItem), 960), (nameof(hBalloonIcon), 968),
                };

                foreach ((string field, int offset) in expected)
                {
                    long actual = Marshal.OffsetOf<NOTIFYICONDATA>(field).ToInt64();
                    if (actual != offset)
                        throw new MarshalDirectiveException(
                            $"NOTIFYICONDATA.{field} 偏移 {actual}，应为 {offset}（与 shellapi.h 不符）");
                }
            }
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

            // 结构体由 ShellNotifyIcon 自己序列化，这里只收指针，避免封送器按 976 补齐
            [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Shell_NotifyIconW")]
            internal static extern bool Shell_NotifyIconW(uint dwMessage, IntPtr lpData);

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

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern bool TranslateMessage(ref MSG lpMsg);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

            [DllImport("user32.dll")]
            internal static extern void PostQuitMessage(int nExitCode);
        }
    }
}
