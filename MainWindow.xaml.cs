using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge
{
    /// <summary>
    /// 主窗口：导航壳 + 关窗去向判定。
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

            // 必须用具名方法订阅：ThemeChanged 是**静态**事件，
            // 用 lambda 会连 this 一起被静态事件表永久持有 —— 窗口 Close 掉也回收不了，
            // 页面上几十 MB 的可视树会一直挂着。这是「仅驻留服务」内存不降的头号元凶。
            ThemeService.ThemeChanged += OnThemeChanged;

            Closed += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;

            StateChanged += (_, _) =>
            {
                MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                // 还原时把最大化补偿的上限放回无限，否则窗口再也拉不高。
                if (WindowState != WindowState.Maximized)
                    MaxHeight = double.PositiveInfinity;
                UpdateUiPresence();
                OnWindowStateChangedForLightMode();
            };

            // 显示 / 隐藏（Show、Hide、收托盘）都要同步，后台组件靠它判断能不能弹提示
            IsVisibleChanged += (_, _) => UpdateUiPresence();

            // 侧栏拖拽的边界情况：ToggleButton 自己吃掉 MouseLeftButtonDown（它要处理
            // IsChecked），却不碰 MouseLeftButtonUp。于是「在侧栏空白处按下开始拖、
            // 在导航项上松手」会直接把 clicked.IsChecked 设成 true 并跳页。
            //
            // 样式写在 ResourceDictionary 里挂不了实例处理器（没有 x:Class），所以改用
            // 类处理器：HandledEventsToo=true 保证即使 ToggleButton 已把事件标记为
            // Handled，我们仍能拿到它并改回 Handled。
            NavPanel.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(NavItem_MouseLeftButtonUp), handledEventsToo: true);

            // 首次导航必须同步点亮对应的导航按钮。
            // 以前这里只调 NavigateTo，而 IsChecked 只在 NavButton_Click 里设过，
            // 于是刚打开软件时页面是调校页、几个导航键却一个都没选中，
            // 用户看不出自己在哪一页（"进程"页尤其明显）。
            Loaded += (_, _) =>
            {
                NavigateToPage(_lastPage);
                UpdateUiPresence();
                ShowVersion();
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

        /// <summary>静态事件 ThemeChanged 的处理函数。之所以是具名方法，因为它必须能被解绑。</summary>
        private void OnThemeChanged(object? sender, EventArgs e)
        {
            WindowHelper.ApplyTitleBarTheme(this, ThemeService.IsDark);
        }

        /// <summary>
        /// 同步「用户看得见主窗口」的状态。后台组件读这个 bool 判断能否弹提示，
        /// 这样它们就不必引用 App（那会把 WPF 拖进纯后台进程）。
        /// </summary>
        private void UpdateUiPresence()
        {
            UiPresence.MainWindowVisible = IsVisible && WindowState != WindowState.Minimized;
        }

        /// <summary>
        /// 关窗口的去向，只认两项设置，层级写死（③ 是总闸，④ 只决定减不减负）：
        ///
        ///   ③「开启后台服务」关 → 进程彻底退出（监听停、托盘收、单实例锁释放）。
        ///   ③ 开 + ④「仅留服务进程」关 → 只 Hide，界面完整保留，内存一分不省。
        ///   ③ 开 + ④ 开 → 先 Hide，倒计时 3 秒后销毁界面，进程只剩后台服务。
        ///
        /// 写在 OnClosing 里而不是按钮里，是为了一并接住 Alt+F4、系统菜单关闭、
        /// 任务栏右键关闭等所有入口，行为一致。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            // 轻量模式走到「仅驻留服务」时由 CloseAndDestroy() 调 Close()，
            // 那一次必须真的关掉窗口，不能再被下面的逻辑拦回去。
            if (AllowRealClose || App.AllowExit)
            {
                base.OnClosing(e);
                return;
            }

            UiSettings s = UiSettingsStore.Settings;

            if (!s.BackendService)
            {
                StopLightCountdown();

                // 不同步在这里 Shutdown —— 正在 Closing 事件里关窗口会搅乱消息循环。
                // 取消本次关闭后再排队退出，届时 AllowExit 已置位，第二次 OnClosing 直接放行。
                e.Cancel = true;
                LogService.Info("已关闭窗口，进程退出（后台服务未开启）");
                Dispatcher.BeginInvoke(new Action(() => App.ExitApplication()));
                return;
            }

            e.Cancel = true;
            Hide();

            if (!s.LightMode)
            {
                LogService.Info("窗口已隐藏，进程继续在后台运行（仅留服务进程未开启，界面完整保留）");
                return;
            }

            StartLightCountdown(s.LightModeDelaySeconds);
        }

        /// <summary>为 true 时 OnClosing 直接放行。仅供 <see cref="CloseAndDestroy"/> 使用。</summary>
        private bool AllowRealClose { get; set; }

        // ==================== 轻量模式倒计时 ====================

        /// <summary>
        /// 关闭前台后的倒计时。到点执行「轻量模式行为」。
        ///
        /// 注意「闲置」的语义是**关闭前台**（点 × 或最小化），
        /// 不是系统空闲、不是鼠标不动 —— 用户正盯着窗口发呆时绝不能自己把窗口弄没。
        /// </summary>
        private DispatcherTimer? _lightTimer;

        /// <summary>
        /// 开始 / 重置倒计时。重复调用（连点 ×、反复最小化）以最后一次为准。
        /// </summary>
        private void StartLightCountdown(int seconds)
        {
            // 窗口还看得见就不该进轻量：说明用户又回来了
            if (IsVisible && WindowState != WindowState.Minimized)
                return;

            StopLightCountdown();

            _lightTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600))
            };

            _lightTimer.Tick += OnLightTimerTick;
            _lightTimer.Start();
        }

        private void OnLightTimerTick(object? sender, EventArgs e)
        {
            StopLightCountdown();
            EnterLightMode();
        }

        private void StopLightCountdown()
        {
            if (_lightTimer == null)
                return;

            try
            {
                _lightTimer.Stop();
            }
            catch
            {
                // 忽略
            }

            _lightTimer = null;
        }

        /// <summary>最小化进入倒计时；恢复显示时取消。点 × 的路径在 OnClosing 里。</summary>
        private void OnWindowStateChangedForLightMode()
        {
            UiSettings s = UiSettingsStore.Settings;

            if (!s.BackendService || !s.LightMode)
            {
                StopLightCountdown();
                return;
            }

            if (WindowState == WindowState.Minimized)
                StartLightCountdown(s.LightModeDelaySeconds);
            else
                StopLightCountdown();
        }

        /// <summary>
        /// 到点执行「仅留服务进程」：销毁窗口，进程靠 OnExplicitShutdown 活着。
        /// 曾并存的「仅清空页面」选项实测工作集同为个位数 MB，差别落在测量噪声里，已删。
        /// </summary>
        private void EnterLightMode()
        {
            UiSettings s = UiSettingsStore.Settings;

            // 倒计时期间用户可能已经把窗口召回来了，或自己关了总闸
            if (!s.BackendService || !s.LightMode)
                return;

            if (IsVisible && WindowState != WindowState.Minimized)
                return;

            LogService.Info("仅留服务进程：已销毁主窗口");
            CloseAndDestroy();
        }

        /// <summary>
        /// 销毁主窗口但不退出进程。与「点 ×」的区别：点 × 只是 Hide，实例还在；
        /// 这里真正 Close，让 WPF 连同可视树、绑定、页面全部回收，
        /// 之后托盘「打开界面」或双击 exe 会 new 一个新的。
        /// </summary>
        public void CloseAndDestroy()
        {
            StopLightCountdown();

            // 先把引用摘掉：ShowOrCreateMainWindow 靠它判断「要不要新建」，
            // 留着一个正在关闭的窗口会让唤醒逻辑以为界面还在。
            App.ReleaseMainWindow(this);

            try
            {
                while (ContentFrame.CanGoBack)
                    ContentFrame.RemoveBackEntry();

                ContentFrame.Content = null;
                ThemeService.StopSystemMonitor();
            }
            catch (Exception ex)
            {
                LogService.Warn($"销毁窗口前释放页面失败：{ex.Message}");
            }

            try
            {
                Hide();

                AllowRealClose = true;
                Close();
            }
            catch (Exception ex)
            {
                LogService.Warn($"销毁主窗口失败：{ex.Message}");
                AllowRealClose = false;
                return;
            }

            // Close() 只是把销毁排进消息队列，可视树 / HwndSource / 绑定要到本轮之后才真拆完。
            // 等到 ApplicationIdle 再回收，否则收的全是还活着的对象，等于白做。
            // 用局部 dispatcher，别在 lambda 里捕获 this —— 那会把窗口拖到这一轮之后才可回收。
            Dispatcher dispatcher = Dispatcher;
            dispatcher.BeginInvoke(
                new Action(() => MemoryTrimmer.TrimInBackground("仅驻留服务")),
                DispatcherPriority.ApplicationIdle);
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

        /// <summary>
        /// ToggleButton 会吃掉 MouseLeftButtonDown（它要处理 IsChecked），但不动
        /// MouseLeftButtonUp。不拦的话，「在侧栏空白处按下开始拖、在导航项上松手」
        /// 这个边界情况会直接触发导航跳页。
        /// </summary>
        private void NavItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

        /// <summary>
        /// 侧栏拖拽 / 双击最大化。侧栏整片都挂了这个处理器，导航项在模板层
        /// 拦掉 Up 事件，所以除了导航项本身，其它位置都能拖动窗口。
        /// </summary>
        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetMaximized(WindowState != WindowState.Maximized);
                return;
            }

            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            if (WindowState == WindowState.Maximized)
            {
                // 最大化状态下 DragMove 会把窗口「粘」在鼠标左上角跳一下。
                // 先还原，再按鼠标在窗口内的相对位置反推 Left，视觉上才连得上。
                // GetPosition(this) 已折算 DPI，PointToScreen 返回的是物理像素，二者同单位。
                double ratio = ActualWidth > 0 ? e.GetPosition(this).X / ActualWidth : 0.5;
                WindowState = WindowState.Normal;
                Left = PointToScreen(e.GetPosition(this)).X - ActualWidth * ratio;
                Top = 0;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标在 DragMove 之前就已释放（快速点击），XAML 里忽略即可。
            }
        }

        /// <summary>
        /// WindowStyle=None 的窗口最大化时会连任务栏一起盖住，必须用工作区高度校正上限。
        /// 扣 2px 抵消 WindowChrome 给最大化窗口加的外扩边框，否则会溢出到工作区之外。
        /// </summary>
        private void SetMaximized(bool maximize)
        {
            MaxHeight = maximize ? SystemParameters.WorkArea.Height - 2 : double.PositiveInfinity;
            WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
        }

        /// <summary>把程序集版本写到侧栏底部。物理文件版本号才是用户该看的那个。</summary>
        private void ShowVersion()
        {
            try
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                string text = File.Exists(exe)
                    ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion ?? string.Empty
                    : string.Empty;

                // FileVersion 是四段（0.10.0.0），界面上只保留前三段。
                string[] parts = text.Split('.');
                VersionText.Text = parts.Length >= 3
                    ? "v" + string.Join(".", parts[0], parts[1], parts[2])
                    : "v" + text;
            }
            catch
            {
                VersionText.Text = string.Empty;
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            SetMaximized(WindowState != WindowState.Maximized);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 走 Close()，让 OnClosing 统一判断是收进托盘还是真退出。
            // 直接 Shutdown() 会绕过这套判断，把后台监听一并干掉。
            Close();
        }
    }
}
