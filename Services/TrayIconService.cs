using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 托盘图标：进程活着就有一个图标，关窗口只是把界面收起来，不等于退出。
    ///
    /// 用户点「X」的本意是「眼不见为净」，不是「把调校一起关掉」。
    /// 绑核监听活在进程里，进程一死监听就死，所以关窗必须收进托盘而不是退出。
    ///
    /// 实现说明：
    /// · 图标用 System.Windows.Forms.NotifyIcon —— 系统自带、久经考验，
    ///   连资源管理器重启后图标自动重建这种事都替我们处理好了。项目里所有
    ///   WinForms / System.Drawing 类型一律写全名，避免与 WPF 同名类型打架。
    /// · 右键菜单用 WPF ContextMenu 而不是 WinForms ContextMenuStrip：
    ///   后者是浅色系统菜单，会在这套深色 UI 里戳出一个白块。
    /// </summary>
    public static class TrayIconService
    {
        private static System.Windows.Forms.NotifyIcon? _icon;

        /// <summary>图标对象自己留一份引用：NotifyIcon.Dispose() 不保证连图标一起释放。</summary>
        private static System.Drawing.Icon? _iconObject;

        /// <summary>
        /// 菜单宿主：一个 1×1、显示在屏幕外、Alt+Tab 里找不到的窗口。
        ///
        /// 为什么需要它：WPF 的 ContextMenu 必须挂在一个真实窗口上，
        /// 才能装好鼠标钩子、做到「点击别处自动收起」。直接从托盘弹出时
        /// 应用不是前台进程，菜单会赖在屏幕上不消失，所以还要先把宿主
        /// 提到前台。宿主必须**真的 Show 过**——隐藏窗口抢不到前台。
        /// </summary>
        private static Window? _menuHost;
        private static ContextMenu? _menu;
        private static MenuItem? _watchItem;

        /// <summary>
        /// 建立托盘图标。必须在 UI 线程调用，且只调一次。
        /// </summary>
        /// <returns>
        /// true = 托盘可用。false = 图标没建起来，调用方必须按「没有托盘」处理，
        /// 否则关窗时把窗口一藏，进程就变成一个既看不见又退不掉的僵尸。
        /// </returns>
        public static bool EnsureCreated()
        {
            if (_icon != null)
                return true;

            try
            {
                System.Drawing.Icon? icon = LoadIcon();
                if (icon == null)
                {
                    LogService.Error("托盘图标资源缺失，托盘不可用");
                    return false;
                }

                _iconObject = icon;
                _icon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = "ZenForge",
                    Visible = true
                };

                _icon.MouseUp += OnIconMouseUp;
                _icon.DoubleClick += (_, _) => ShowOrCreateMainWindow();

                RefreshMenuState();
                LogService.Info("托盘图标已就绪");
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error($"托盘图标创建失败：{ex.Message}");
                DisposeIcon();
                return false;
            }
        }

        /// <summary>
        /// 把主窗口叫出来。没有主窗口（静默常驻启动 / 轻量模式已销毁窗口）就现建一个。
        ///
        /// 之所以不看 Application.MainWindow：托盘菜单那个 1×1 宿主是第一个 Show 出来的
        /// Window，会被 WPF 记成 MainWindow；轻量模式「仅驻留服务」之后，它又指向一个
        /// 已经 Closed 的残骸。主窗口实例统一由 App 持有，这里只信 App.CurrentMainWindow。
        /// </summary>
        public static void ShowOrCreateMainWindow()
        {
            Application? app = Application.Current;
            if (app == null)
                return;

            try
            {
                MainWindow? main = App.CurrentMainWindow;

                if (main == null)
                {
                    main = new MainWindow();
                    App.SetMainWindow(main);

                    // WPF 仍需要一个「主窗口」概念给对话框当 Owner，
                    // 但 App.RealMainWindow 优先认自己那份引用。
                    app.MainWindow = main;
                }

                bool wasHidden = !main.IsVisible;

                if (wasHidden)
                    main.Show();

                if (main.WindowState == WindowState.Minimized)
                    main.WindowState = WindowState.Normal;

                main.Activate();
                WindowHelper.ForceForeground(main);

                // 隐藏再显示的窗口偶尔会带着半截残影，主动作废重绘一次
                if (wasHidden)
                    main.ForceRepaint();
            }
            catch (Exception ex)
            {
                LogService.Error($"唤出主窗口失败：{ex.Message}");
            }
        }

        /// <summary>监听状态变了，刷新菜单项与悬浮提示。</summary>
        public static void RefreshMenuState()
        {
            if (_icon == null)
                return;

            bool running = ProcessWatcher.IsRunning;

            try
            {
                _icon.Text = running
                    ? "ZenForge · 后台绑核运行中"
                    : "ZenForge · 后台绑核已暂停";
            }
            catch
            {
                // 忽略
            }

            if (_watchItem != null)
                _watchItem.Header = running ? "暂停绑核" : "恢复绑核";
        }

        /// <summary>退出前收摊：隐藏并释放图标，关掉宿主窗口。</summary>
        public static void Shutdown()
        {
            DisposeIcon();

            _watchItem = null;

            try
            {
                _menuHost?.Close();
            }
            catch
            {
                // 忽略
            }

            _menuHost = null;
            _menu = null;
        }

        /// <summary>
        /// 按设置显示或隐藏托盘图标。隐藏后进程仍常驻，但界面入口只剩双击 exe。
        /// 与 <see cref="Shutdown"/> 的区别是语义：这里是用户选择的「不显示」，
        /// 不是进程退场。
        /// </summary>
        public static void SetVisible(bool visible)
        {
            if (visible)
            {
                EnsureCreated();
                return;
            }

            Shutdown();
            LogService.Info("托盘图标已隐藏（后台仍常驻，双击 ZenForge.exe 打开界面）");
        }

        private static void DisposeIcon()
        {
            try
            {
                if (_icon != null)
                {
                    _icon.MouseUp -= OnIconMouseUp;
                    _icon.Visible = false;
                    _icon.Icon = null;
                    _icon.Dispose();
                }
            }
            catch
            {
                // 忽略
            }

            try
            {
                _iconObject?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _icon = null;
            _iconObject = null;
        }

        private static void OnIconMouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
                ShowMenu();
            else if (e.Button == System.Windows.Forms.MouseButtons.Left)
                ShowOrCreateMainWindow();
        }

        private static void ShowMenu()
        {
            Application? app = Application.Current;
            if (app == null)
                return;

            try
            {
                _menu ??= BuildMenu(app);
                if (_menu == null)
                    return;

                EnsureMenuHost();

                if (_menuHost != null)
                {
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(_menuHost).Handle;
                    if (hwnd != IntPtr.Zero)
                        WindowHelper.ForceForeground(_menuHost);
                }

                RefreshMenuState();

                _menu.PlacementTarget = _menuHost;
                _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                _menu.StaysOpen = false;
                _menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                LogService.Error($"托盘菜单打开失败：{ex.Message}");
            }
        }

        private static ContextMenu? BuildMenu(Application app)
        {
            try
            {
                ContextMenu menu = new();

                // 样式从主题里取，保证深色/浅色都跟得上
                if (app.TryFindResource("DarkContextMenuStyle") is Style menuStyle)
                    menu.Style = menuStyle;
                if (app.TryFindResource("DarkMenuItemStyle") is Style itemStyle)
                    menu.ItemContainerStyle = itemStyle;

                MenuItem open = new() { Header = "打开界面" };
                open.Click += (_, _) => ShowOrCreateMainWindow();

                _watchItem = new MenuItem { Header = "暂停绑核" };
                _watchItem.Click += (_, _) => ToggleWatch();

                MenuItem exit = new() { Header = "退出" };
                exit.Click += (_, _) => App.ExitApplication();

                Separator separator = new();
                if (app.TryFindResource("DarkMenuSeparatorStyle") is Style separatorStyle)
                    separator.Style = separatorStyle;

                menu.Items.Add(open);
                menu.Items.Add(_watchItem);
                menu.Items.Add(separator);
                menu.Items.Add(exit);

                // 菜单关掉后把前台还给主窗口。
                // 不还的话前台会停在屏幕外那个宿主上，用户点回主窗口时键盘没反应。
                menu.Closed += (_, _) =>
                {
                    Window? main = App.RealMainWindow;
                    if (main != null && main.IsVisible)
                        WindowHelper.ForceForeground(main);
                };

                return menu;
            }
            catch (Exception ex)
            {
                LogService.Error($"托盘菜单构建失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>第一次用菜单时才把宿主窗口显示出来，避免启动时多开一个窗口。</summary>
        private static void EnsureMenuHost()
        {
            if (_menuHost != null)
                return;

            try
            {
                Window host = new()
                {
                    Width = 1,
                    Height = 1,
                    Left = -10000,
                    Top = -10000,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    // 刻意不设 Focusable=false：菜单关闭后焦点要还回宿主再还给主窗口，
                    // 宿主不可聚焦的话焦点会掉到不可见窗口上，键盘就不响应了。
                    IsTabStop = false,
                    FocusVisualStyle = null
                };

                host.SourceInitialized += (_, _) => WindowHelper.MarkAsToolWindow(host);

                Application? app = Application.Current;
                Window? mainBefore = app?.MainWindow;

                host.Show();

                // WPF 把第一个 Show 出来的 Window 记成 Application.MainWindow。
                // 静默模式下主窗口还没建，宿主一冒头就会顶替掉这个位置，
                // 后面谁拿 MainWindow 当宿主（弹窗 Owner、导航目标）都会跑到屏幕外。
                // 这里把它还原回原样。
                if (app != null && !ReferenceEquals(app.MainWindow, mainBefore))
                {
                    try
                    {
                        app.MainWindow = mainBefore;
                    }
                    catch
                    {
                        // MainWindow 不允许置空时保持现状，ShowOrCreateMainWindow
                        // 里还有一层 ReferenceEquals 兜底
                    }
                }

                _menuHost = host;
            }
            catch (Exception ex)
            {
                LogService.Error($"托盘菜单宿主创建失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 暂停 / 恢复绑核。
        ///
        /// 改完紧接着把设置写回 ui.json，让托盘菜单和设置页那个开关不会各说各话。
        /// </summary>
        private static void ToggleWatch()
        {
            try
            {
                if (ProcessWatcher.IsRunning)
                    ProcessWatcher.Stop();
                else
                    ProcessWatcher.Start();

                bool running = ProcessWatcher.IsRunning;

                UiSettingsStore.Settings.WatchProcessBinding = running;
                UiSettingsStore.Save();

                LogService.Info(running ? "托盘：已恢复进程绑核监听" : "托盘：已暂停进程绑核监听");
                RefreshMenuState();
            }
            catch (Exception ex)
            {
                LogService.Error($"托盘切换绑核监听失败：{ex.Message}");
            }
        }

        /// <summary>图标来源：内嵌资源优先，回退 exe 自身图标。</summary>
        private static System.Drawing.Icon? LoadIcon()
        {
            try
            {
                System.Windows.Resources.StreamResourceInfo? info = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/ZenForge.ico", UriKind.Absolute));

                if (info != null)
                    return new System.Drawing.Icon(info.Stream, 16, 16);
            }
            catch (Exception ex)
            {
                LogService.Warn($"读取内嵌图标失败：{ex.Message}");
            }

            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    return System.Drawing.Icon.ExtractAssociatedIcon(exe);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"提取 exe 图标失败：{ex.Message}");
                }
            }

            return null;
        }
    }
}
