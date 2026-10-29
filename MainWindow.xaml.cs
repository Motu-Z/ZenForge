using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, _) =>
            {
                WindowHelper.ApplyWindowAttributes(this);
                WindowHelper.ApplyTitleBarTheme(this, ThemeService.IsDark);
            };

            ThemeService.ThemeChanged += (_, _) =>
                WindowHelper.ApplyTitleBarTheme(this, ThemeService.IsDark);

            StateChanged += (_, _) =>
            {
                MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                UpdateUiPresence();
            };

            // 显示 / 隐藏（Show、Hide、收托盘）都要同步，后台组件靠它判断能不能弹提示
            IsVisibleChanged += (_, _) => UpdateUiPresence();

            // 初次导航必须同步点亮对应的导航按钮。
            // 以前这里只调 NavigateTo，而 IsChecked 只在 NavButton_Click 里设过，
            // 于是刚打开软件时页面是调校页、四个导航键却一个都没选中，
            // 用户看不出自己在哪一页（"调度"页尤其明显）。
            Loaded += (_, _) =>
            {
                NavigateToPage(_lastPage);
                UpdateUiPresence();
            };

            // 软件渲染 + 每显示器 DPI 下，窗口偶尔会只把首帧画出一部分，
            // 剩下的区域留着桌面（或上一次的残影）。
            // 首帧出来之后主动作废一次视觉并重排，成本只有一次。
            ContentRendered += (_, _) =>
                Dispatcher.BeginInvoke(new Action(ForceRepaint), DispatcherPriority.Loaded);
        }

        /// <summary>作废并重排一次窗口内容，用来修掉偶发的半截渲染。</summary>
        public void ForceRepaint()
        {
            try
            {
                InvalidateVisual();
                UpdateLayout();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 同步「用户是否看得见主窗口」。后台组件靠它决定要不要弹提示，
        /// 也靠它避免直接引用 App（那会把 WPF 拖进纯后台进程）。
        /// </summary>
        private void UpdateUiPresence()
        {
            UiPresence.MainWindowVisible = IsVisible && WindowState != WindowState.Minimized;
        }

        /// <summary>
        /// 关窗的真正语义在这里决定。
        ///
        /// 过去这里直接 Shutdown()，绑核监听随之消失 —— 用户点 X 只是想让窗口消失，
        /// 结果整个调校一起被关掉了。
        ///
        /// 现在只认「关闭窗口时」这一项设置：
        ///   · 始终收进托盘 / 智能且监听在跑 → 进程留下
        ///   · 关闭即退出 / 智能且没在监听 → 真退出
        ///
        /// 托盘有没有图标**不参与**这个判断。用户关掉托盘图标后，进程照样按设置留下，
        /// 只是没有托盘入口 —— 想再看到界面就双击 exe。以前这里还要求
        /// TryHideToTray() 为真，导致关掉托盘后点 X 就直接退进程，
        /// 与设置页写的「关闭窗口时：始终收进托盘」自相矛盾。
        ///
        /// 用 OnClosing 而不是在按钮里硬写，是为了一并接住 Alt+F4、
        /// 系统菜单关闭、任务栏右键关闭等所有关闭入口，行为一致。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (App.AllowExit)
            {
                base.OnClosing(e);
                return;
            }

            if (App.ShouldStayInBackground())
            {
                e.Cancel = true;
                Hide();
                ReleaseForTray();

                if (Services.TrayIconService.TryHideToTray())
                {
                    Services.TrayIconService.NotifyMinimizedOnce();
                    LogService.Info("主窗口已收进托盘，进程绑核监听继续在后台运行");
                }
                else
                {
                    // 托盘点过「不显示」：藏起来之后确实没有入口了，
                    // 但这是用户自己的设定组合，不替他改主意，只把话说明白。
                    LogService.Info(
                        "主窗口已隐藏，进程继续在后台运行（托盘图标已关闭，双击 ZenForge.exe 可唤出界面）");
                }

                return;
            }

            // 不同步在这里 Shutdown —— 正在 Closing 事件里关窗口会搅乱消息循环。
            // 取消本次关闭后再排队退出，届时 AllowExit 已置位，第二次 OnClosing 直接放行。
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => App.ExitApplication()));
        }

        /// <summary>启动时停留的页面。</summary>
        private const string DefaultPage = "Views/TuningPage.xaml";

        /// <summary>当前停留的页面，收进托盘后要凭它恢复。</summary>
        private string _lastPage = DefaultPage;

        private void NavigateToPage(string relativeUri)
        {
            _lastPage = relativeUri;
            ContentFrame.Navigate(new Uri(relativeUri, UriKind.Relative));
            SetActiveNav(relativeUri);
        }

        /// <summary>
        /// 收进托盘时把页面整棵树拆掉。
        ///
        /// 收进托盘意味着「调完了，不用再看了」。一个 WPF 页面的可视树连同它背后的
        /// 进程列表、掩码列表，动辄几十 MB —— 与其让它们陪着后台监听一起空转，
        /// 不如直接还回去。绑核监听活在 Services 里，不依赖任何页面。
        /// </summary>
        public void ReleaseForTray()
        {
            try
            {
                // 导航日志里存着页面入口，不清掉 Frame 会一直攥着它们
                while (ContentFrame.CanGoBack)
                    ContentFrame.RemoveBackEntry();

                ContentFrame.Content = null;

                // 没人看界面了，主题 / 强调色轮询也停下
                ThemeService.StopSystemMonitor();
            }
            catch (Exception ex)
            {
                LogService.Warn($"释放页面失败：{ex.Message}");
            }

            // 真回收放到后台做：GC 会阻塞，别卡在关窗这一下。
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true);
                    System.GC.WaitForPendingFinalizers();
                }
                catch
                {
                    // 忽略
                }
            });
        }

        /// <summary>从托盘回来时把页面重新建起来，回到上次停留的那一页。</summary>
        public void RestoreFromTray()
        {
            try
            {
                if (ContentFrame.Content == null)
                {
                    // 托盘期间可能换过主题，回到界面对一次表再建页面
                    ThemeService.StartSystemMonitor();
                    NavigateToPage(_lastPage);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"恢复页面失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 把某个导航按钮设为选中态，其余取消。
        /// Tag 里存的就是各按钮对应的页面路径（见 MainWindow.xaml）。
        /// </summary>
        private void SetActiveNav(string pageUri)
        {
            foreach (var child in NavPanel.Children)
            {
                if (child is ToggleButton button)
                    button.IsChecked = button.Tag as string == pageUri;
            }
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            clicked.IsChecked = true;
            NavigateToPage((string)clicked.Tag);   // 同时把其余按钮取消选中
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 走 Close()，让 OnClosing 统一判断是收进托盘还是真退出。
            // 直接 Shutdown() 会绕过这套判断，把后台监听一并干掉。
            Close();
        }
    }
}
