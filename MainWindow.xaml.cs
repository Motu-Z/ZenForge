using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows.Threading;
using ZenForge.Helpers;
using ZenForge.Services;
using ZenForge.Views;

namespace ZenForge
{
    /// <summary>
    /// 主窗口：导航壳 + 关窗去向判定。
    ///
    /// 【v1.0.2 的两处结构性改动】
    /// 1. Frame → ContentControl + <see cref="NavigationRouter"/>：
    ///    Frame 的导航日志会强引用已卸载的 Page，切几次页面就是好几份
    ///    旧视觉树留在内存里；路由只保留一份实例并按 key 复用。
    /// 2. 定时器统一由 <see cref="PageTimerManager"/> 管：切页时非当前页的
    ///    定时器全部挂起，窗口隐藏 / 最小化时连全局定时器一起停 ——
    ///    这是「后台纯待机 0% CPU」的执行点。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>启动时停留的页面。</summary>
        private const string DefaultPage = TuningPage.PageKey;

        /// <summary>唯一的页面切换入口。</summary>
        private readonly NavigationRouter _router;

        /// <summary>当前停留的页面 key，收进托盘后要凭它恢复。</summary>
        private string _lastPage = DefaultPage;

        public MainWindow()
        {
            InitializeComponent();

            _router = new NavigationRouter(PageHost);
            RegisterPages();

            UiTextOptions.Attach(this);

            // 主题/强调色轮询跟着窗口走：CloseAndDestroy 里会把它停掉，
            // 重新打开界面时必须重新挂起来 —— 否则第二次打开之后，
            // 改系统主题界面不跟了，用户会以为「重启才好」。
            // 这个方法是幂等的（内部先 Dispose 旧定时器）。
            ThemeService.StartSystemMonitor();

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

            // 显示 / 隐藏（Show、Hide、收托盘）都要同步：
            // 后台组件靠它判断能不能弹提示，定时器靠它决定要不要停。
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

        /// <summary>
        /// 登记七个页面。
        ///
        /// 页面在第一次导航到它时才被创建（工厂延迟执行），
        /// 所以启动时只实例化调校页 —— 进程页那种带几百行模板的页面
        /// 不会在用户还没点它的时候就把内存吃掉。
        /// </summary>
        private void RegisterPages()
        {
            _router.Register(TuningPage.PageKey, () => new TuningPage());
            _router.Register(ProcessBindingPage.PageKey, () => new ProcessBindingPage());
            _router.Register(ProfilePage.PageKey, () => new ProfilePage());
            _router.Register(GeneralPage.PageKey, () => new GeneralPage());
            _router.Register(BehaviorPage.PageKey, () => new BehaviorPage());
            _router.Register(AdvancedPage.PageKey, () => new AdvancedPage());
            _router.Register(LogPage.PageKey, () => new LogPage());
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
        /// 同步「用户看得见主窗口」的状态。
        ///
        /// 这一处是**后台零占用的总闸门**：窗口一看不见，下列东西全部停摆 ——
        ///   · PageTimerManager：所有受管定时器（含 2 秒一次的主题监视器）；
        ///   · ProcessRefresher：进程采样循环（硬闸门，不依赖页面引用计数）；
        ///   · ProcessWatcher：WMI 不可用时的降级全量轮询。
        ///
        /// WMI **事件**模式刻意不停：它是操作系统的进程创建事件，闲时成本≈0，
        /// 而且「后台常驻调度」只在窗口看不见时才有意义 —— 停掉它等于关功能。
        /// </summary>
        private void UpdateUiPresence()
        {
            bool visible = IsVisible && WindowState != WindowState.Minimized;

            UiPresence.MainWindowVisible = visible;

            // 三道闸门一起推。顺序无所谓，但必须一次推完：
            // 只推其中一道就会出现「停了 A 还有 B 在跑」的错觉。
            PageTimerManager.SetHostVisible(visible);
            ProcessRefresher.SetHostVisible(visible);
            ProcessWatcher.SetHostVisible(visible);

            LogService.Info(visible
                ? $"窗口可见：已恢复后台刷新（受管定时器 {PageTimerManager.RunningCount} 个在跑）"
                : $"窗口不可见：已挂起全部 UI 刷新与采样（受管定时器 {PageTimerManager.RunningCount} 个在跑，" +
                  $"采样观察者 {(ProcessRefresher.IsWatched ? "有" : "无")}，" +
                  $"绑核监听 {(ProcessWatcher.IsRunning ? (ProcessWatcher.UsingEvents ? "WMI 事件" : "降级轮询") : "未运行")}）");
        }

        /// <summary>
        /// 关窗口的去向，只认一项设置（v1.2.0 起）：
        ///
        ///   「开启后台服务」关 → 进程彻底退出（监听停、托盘收、单实例锁释放）。
        ///   「开启后台服务」开 → **立刻销毁窗口**（可视树、页面、绑定全部回收），
        ///                        进程只剩后台服务，托盘图标仍在。
        ///
        /// 【为什么删掉了「只 Hide」这条路】
        /// 那是 v1.0.x 的「秒开」优化：点 × 只隐藏，下次打开瞬间可见。
        /// 代价是一整棵 WPF 可视树常驻 —— 实测 74MB，而早期版本只有 3~8MB。
        /// 用户的判断是对的：为了省几百毫秒，让一个后台绑核工具常驻几十 MB 内存，
        /// 这笔账不划算。现在点 × 就是真的拆，下次双击全新实例化。
        ///
        /// 「后台轻量模式」这个开关因此退化成**只作用于最小化**（见
        /// <see cref="OnWindowStateChangedForLightMode"/>）：关闭窗口已经不需要它了。
        ///
        /// 写在 OnClosing 里而不是按钮里，是为了一并接住 Alt+F4、系统菜单关闭、
        /// 任务栏右键关闭等所有入口，行为一致。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            // 轻量模式走到「仅留服务进程」时由 CloseAndDestroy() 调 Close()，
            // 那一次必须真的关掉窗口，不能再被下面的逻辑拦回去。
            if (AllowRealClose || App.AllowExit)
            {
                base.OnClosing(e);
                return;
            }

            if (!UiSettingsStore.Settings.BackendService)
            {
                StopLightCountdown();

                // 不同步在这里 Shutdown —— 正在 Closing 事件里关窗口会搅乱消息循环。
                // 取消本次关闭后再排队退出，届时 AllowExit 已置位，第二次 OnClosing 直接放行。
                e.Cancel = true;
                LogService.Info("已关闭窗口，进程退出（后台服务未开启）");
                Dispatcher.BeginInvoke(new Action(() => App.ExitApplication()));
                return;
            }

            // 先取消这一次，再在消息循环里走销毁流程。
            // 直接在 Closing 里 Close 会让 WPF 在同一个 Closing 派发中重入。
            e.Cancel = true;
            LogService.Info("已关闭窗口：销毁界面，仅保留后台服务（下次打开为全新实例）");
            Dispatcher.BeginInvoke(new Action(CloseAndDestroy));
        }

        /// <summary>为 true 时 OnClosing 直接放行。仅供 <see cref="CloseAndDestroy"/> 使用。</summary>
        private bool AllowRealClose { get; set; }

        /// <summary>销毁是否已经开始（连点 × 时挡住第二次）。</summary>
        private bool _destroying;

        // ==================== 轻量模式倒计时 ====================

        /// <summary>
        /// 关闭前台后的倒计时。到点执行「轻量模式行为」。
        ///
        /// 注意「闲置」的语义是**关闭前台**（点 × 或最小化），
        /// 不是系统空闲、不是鼠标不动 —— 用户正盯着窗口发呆时绝不能自己把窗口弄没。
        ///
        /// 它不属于任何页面，所以用 <see cref="PageTimerManager.HostScope"/>：
        /// 窗口还能看见时它必须跑，窗口一隐藏就连它一起停。
        /// </summary>
        private ManagedTimer? _lightTimer;

        /// <summary>
        /// 开始 / 重置倒计时。重复调用（连点 ×、反复最小化）以最后一次为准。
        /// </summary>
        private void StartLightCountdown(int seconds)
        {
            // 窗口还看得见就不该进轻量：说明用户又回来了
            if (IsVisible && WindowState != WindowState.Minimized)
                return;

            StopLightCountdown();

            _lightTimer = PageTimerManager.Create(
                PageTimerManager.HostScope,
                TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600)),
                OnLightTimerTick);

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

            _lightTimer.Dispose();
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
            // 幂等闸门。连点 × 会排两次销毁：第二次进来时窗口已经 Closed，
            // 再 Hide() 会抛「窗口已关闭」，然后被 catch 记一条 Warn ——
            // 用户什么都没做错，日志里却多一条吓人的警告。
            if (_destroying)
                return;

            _destroying = true;

            StopLightCountdown();

            // 先把引用摘掉：ShowOrCreateMainWindow 靠它判断「要不要新建」，
            // 留着一个正在关闭的窗口会让唤醒逻辑以为界面还在。
            App.ReleaseMainWindow(this);

            try
            {
                // 逐个销毁页面，页面自己释放受管定时器并退订服务。
                // 少了这一步，页面实例会被路由字典一直持有，
                // 「仅留服务进程」省下的那部分内存一分都回收不了。
                _router.Clear();

                // 主题轮询先停：它属于宿主作用域，ClearAll 也会收掉它，
                // 但显式停一次能保证「窗口没了就不再有人读注册表」这条读得出来。
                ThemeService.StopSystemMonitor();

                // 兜底：页面理论上都在 OnDeactivated 里 Dispose 了自己的定时器，
                // 但「理论上」在内存问题上不值钱。窗口都要没了，全部收掉。
                PageTimerManager.ClearAll();

                // Toast 宿主是静态引用，而它在主窗口的可视树里 ——
                // 不清这一条，静态 -> ToastHost -> 视觉父级 -> 整个窗口，
                // 一路强引用把刚销毁的窗口又拽回来。Unloaded 大部分时候会触发，
                // 但窗口 Close 的路径上不保证，所以显式断一次。
                ToastService.Detach();
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

            // Application.MainWindow 是**强引用**，Close() 不会替你清。
            // 不清的话窗口对象连同整棵可视树一直挂到下一次 SetMainWindow，
            // 期间 GC 一次都收不掉 —— 这正是「窗口销毁了内存没降」的第二个来源
            // （第一个是 ToastService.Host）。
            try
            {
                Application? app = Application.Current;
                if (app != null && ReferenceEquals(app.MainWindow, this))
                    app.MainWindow = null!;
            }
            catch (Exception ex)
            {
                LogService.Warn($"清空 Application.MainWindow 失败：{ex.Message}");
            }

            // Close() 只是把销毁排进消息队列，可视树 / HwndSource / 绑定要到本轮之后才真拆完。
            // 等到 ApplicationIdle 再回收，否则收的全是还活着的对象，等于白做。
            // 用局部 dispatcher，别在 lambda 里捕获 this —— 那会把窗口拖到这一轮之后才可回收。
            Dispatcher dispatcher = Dispatcher;
            dispatcher.BeginInvoke(
                new Action(() => MemoryTrimmer.TrimInBackground("窗口销毁")),
                DispatcherPriority.ApplicationIdle);

            // 再补一次延迟回收：Close 之后仍会有 WMI 回调、终结器、日志落盘线程
            // 在这几百毫秒里分配对象，第一次 Trim 收到的是「还在动」的堆。
            // 3 秒后这次是真正的静息值，任务管理器里看的就是它。
            // 3 秒内用户可能又把界面叫回来了，那就别再搅一次工作集：
            // 回收本身不危险（缺页会再换进来），但没理由在别人正用的时候抖一下。
            Task.Delay(TimeSpan.FromSeconds(3))
                .ContinueWith(
                    _ =>
                    {
                        if (App.CurrentMainWindow == null)
                            MemoryTrimmer.TrimInBackground("窗口销毁·静息");
                    },
                    TaskScheduler.Default);
        }

        // ==================== 导航 ====================

        private void NavigateToPage(string key)
        {
            _lastPage = key;

            // 页面切换的全部副作用都在路由里：旧页 OnDeactivated（停定时器）→
            // 换 Content → SetActivePage（非当前页的定时器一起挂起）→ 新页 OnActivated。
            _router.Navigate(key);

            SetActiveNav(key);
        }

        /// <summary>
        /// 把某个导航按钮设为选中态，其余取消。
        /// Tag 里存的就是各按钮对应的路由 key（见 MainWindow.xaml）。
        /// </summary>
        private void SetActiveNav(string key)
        {
            foreach (object child in NavPanel.Children)
            {
                if (child is ToggleButton button)
                    button.IsChecked = button.Tag as string == key;
            }
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            clicked.IsChecked = true;
            NavigateToPage(clicked.Tag as string ?? DefaultPage);   // 同时把其余按钮取消选中
        }

        /// <summary>
        /// ToggleButton 会吃掉 MouseLeftButtonDown（它要处理 IsChecked），但不动
        /// MouseLeftButtonUp。不拦的话，「在侧栏空白处按下开始拖、在导航项上松手」
        /// 这个边界情况会直接触发导航跳页。
        /// </summary>
        private void NavItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

        /// <summary>
        /// 内容区顶部拖拽栏：按住可拖窗口，双击最大化 / 还原。
        ///
        /// 与 <see cref="Chrome_MouseLeftButtonDown"/> 的差别只在「还原后的落点」：
        /// 拖拽栏横贯内容区，用户在哪一处按下，还原后窗口的左上角就该落在
        /// 那一点的左侧同样比例的位置。两处共用同一套换算式，避免出现
        /// 「从侧栏拖和从右上角拖，还原位置不一样」。
        /// </summary>
        private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetMaximized(WindowState != WindowState.Maximized);
                e.Handled = true;
                return;
            }

            BeginDrag(e);
        }

        /// <summary>
        /// 按住侧栏：拖窗口 / 双击最大化。侧栏整片都挂了这个处理器，导航项在
        /// MainWindow 的类处理器里拦掉 Up 事件，所以除了导航项本身，其它位置都能拖动窗口。
        /// </summary>
        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetMaximized(WindowState != WindowState.Maximized);
                return;
            }

            BeginDrag(e);
        }

        /// <summary>
        /// 拖拽的公共实现：最大化状态下先还原，再按鼠标在窗口内的相对位置反推 Left。
        ///
        /// 直接对最大化窗口调 DragMove 会把窗口「粘」在鼠标左上角跳一下 ——
        /// 系统先把它还原成 Normal，尺寸和位置都由系统决定，看起来就是瞬移。
        /// 这里手动还原并换算 Left，视觉上才连得上。
        /// </summary>
        private void BeginDrag(MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            if (WindowState == WindowState.Maximized)
            {
                // GetPosition(this) 已折算 DPI，PointToScreen 返回物理像素，二者同单位。
                // ratio 夹在 0.05..0.95：鼠标正好压在窗口极边缘时，纯按比例算会把
                // 还原后的窗口推到屏幕外，留一点余量。
                double ratio = ActualWidth > 0 ? e.GetPosition(this).X / ActualWidth : 0.5;
                ratio = Math.Clamp(ratio, 0.05, 0.95);

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
                // 鼠标在 DragMove 之前就已释放（快速点击），忽略即可。
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
