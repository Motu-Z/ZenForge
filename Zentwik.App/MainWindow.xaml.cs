using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.Services;
using Zentwik.App.ViewModels;
using Zentwik.App.Views;
using Zentwik.Core.Services;
using Windows.Graphics;

namespace Zentwik.App;

/// <summary>
/// 主窗口 = 外壳。只负责三件事：标题栏与材质动效、页面导航、窗口可见性广播。
///
/// 【页面为什么是 new 出来的，而不是 Frame.Navigate(typeof(T))】
/// Navigate 要求页面有无参构造。一旦页面要构造函数注入 ViewModel，
/// 就只能退化成「在页面里再解析一次容器」——那等于把服务定位器写进每一页。
/// 这里由外壳持有 ViewModel 并显式 new 页面，注入链保持干净，
/// 页面实例还顺便获得了缓存（切页不重建、滚动位置与筛选状态不丢）。
///
/// 【标题栏为什么一行拖拽代码都没有】
/// <c>ExtendsContentIntoTitleBar</c> + <c>SetTitleBar</c> 把整条 48px 交给系统的
/// 非客户区命中测试：拖动、双击最大化、拖到屏幕顶部的贴边分屏、边缘拉伸全部由
/// DWM / 外壳处理。自己接 PointerPressed 做 DragMove 会把这些一起做掉，
/// 而且拖动时会丢帧（每帧都要跨进程同步窗口位置）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();
    private readonly ThemeController _theme;
    private readonly SettingsViewModel _settings;
    private readonly TuningViewModel _tuning;
    private readonly ProcessViewModel _process;
    private readonly ConfigViewModel _config;
    private readonly BehaviorViewModel _behavior;
    private readonly AdvancedViewModel _advanced;
    private readonly LogViewModel _log;
    private readonly ILogService _logger;
    private readonly IProcessWatcher _watcher;

    private bool _clientSized;

    public MainWindow(
        ThemeController theme,
        SettingsViewModel settings,
        TuningViewModel tuning,
        ProcessViewModel process,
        ConfigViewModel config,
        BehaviorViewModel behavior,
        AdvancedViewModel advanced,
        LogViewModel log,
        ILogService logger,
        IProcessWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        ArgumentNullException.ThrowIfNull(behavior);
        _behavior = behavior;
        ArgumentNullException.ThrowIfNull(advanced);
        _advanced = advanced;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(watcher);
        _watcher = watcher;

        InitializeComponent();

        AppWindow.Title = "Zentwik";

        // 顺序是硬约束，不能调换：
        //   PreferredHeightOption 只在「内容已延伸到标题栏区域」时才有意义，
        //   在 ExtendsContentIntoTitleBar 之前设会抛 COMException 0x8007139F。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        // ==================== 自绘窗口控制按钮 ====================
        // 规范要求三键悬浮在距顶/右 12px 处且圆角 6px，系统按钮做不到。
        // 接管分两步，缺一不可：
        //   1. 系统按钮染成全透明（否则自绘按钮下面还压着一套系统图标）；
        //   2. 用 InputNonClientPointerSource 把系统的 min/max/close 命中区域置空，
        //      否则透明按钮仍会吃掉右上角的点击。代价是放弃 Snap Layouts —— 已获批准。
        TitleBarColors.Transparent(AppWindow.TitleBar);

        try
        {
            InputNonClientPointerSource ncSource =
                InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            // 传空数组 = 清空该区域：系统按钮不再吃掉右上角任何点击。
            RectInt32[] emptyRegions = Array.Empty<RectInt32>();
            ncSource.SetRegionRects(NonClientRegionKind.Minimize, emptyRegions);
            ncSource.SetRegionRects(NonClientRegionKind.Maximize, emptyRegions);
            ncSource.SetRegionRects(NonClientRegionKind.Close, emptyRegions);
        }
        catch (Exception ex)
        {
            // 旧系统版本可能没有该 API：失败时系统按钮仍以透明态存在，
            // 右上角点击会落进原区域 —— 功能不缺失，只是视觉上多一层透明命中区。
            _logger.Warn($"非客户区接管失败（退化为透明系统按钮）：{ex.Message}");
        }

        // 最大化状态切换 □ / ❐ 图标（拖拽标题栏到顶部、Win+Up 等系统路径也要响应）。
        AppWindow.Changed += OnAppWindowChanged;
        UpdateMaximizeIcon();

        // 主题、材质、动效全在 ThemeController 里，外壳不重复一份。
        _theme.Attach(this);
        _settings.Initialize();

        VersionText.Text = DescribeVersion();

        Activated += OnActivated;
        Closed += OnClosed;

        // 默认落在调校页：这是用户打开工具 90% 的目的地。
        // 索引不能写死 —— 导航里插了分隔项，索引会随分组变化而错位。
        Nav.SelectedItem = FindNavItem("tuning");
        Navigate("tuning");

        _logger.Info($"主窗口已就绪：材质 {_theme.CurrentBackdrop}，动效 {_theme.CurrentMotion}");
    }

    // ==================== 窗口控制按钮 ====================

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => (AppWindow.Presenter as OverlappedPresenter)?.Minimize();

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Maximized)
            presenter.Restore();
        else
            presenter.Maximize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>窗口位置 / 尺寸变化（含系统路径的最大化、还原）后同步 □ / ❐ 图标。</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
            UpdateMaximizeIcon();
    }

    private void UpdateMaximizeIcon()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 把系统标题栏按钮整体染成透明。ThemeController 的配色逻辑仍保留
    /// （万一接管失败需要回退时改回可见即可），这里只在自绘接管成功路径上清场。
    /// </summary>
    private static class TitleBarColors
    {
        public static void Transparent(Microsoft.UI.Windowing.AppWindowTitleBar titleBar)
        {
            titleBar.ButtonForegroundColor = Colors.Transparent;
            titleBar.ButtonHoverForegroundColor = Colors.Transparent;
            titleBar.ButtonPressedForegroundColor = Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = Colors.Transparent;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
            titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
    }

    // ==================== 窗口尺寸 ====================

    /// <summary>
    /// 按 1120×780 的 DIP 尺寸设置客户区。
    ///
    /// 缩放比走 Win32 <c>GetDpiForWindow</c>，不用 <c>XamlRoot.RasterizationScale</c>：
    /// 后者在 Loaded 阶段尚未初始化（还是 1.0），拿它算像素会在高 DPI 机器上
    /// 得到「窗口按 100% 开、内容按 150% 画」的结果，整页横向溢出。
    /// </summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_clientSized)
            return;

        _clientSized = true;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = NativeWindow.GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Round(1180 * scale),
            (int)Math.Round(820 * scale)));
    }

    // ==================== 导航 ====================

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string key })
            Navigate(key);
    }

    /// <summary>按 Tag 取导航项。索引会因分隔项增删而错位，只有 Tag 是稳定标识。</summary>
    private ListBoxItem? FindNavItem(string key)
    {
        foreach (object item in Nav.Items)
        {
            if (item is ListBoxItem { Tag: string tag } container && tag == key)
                return container;
        }

        return null;
    }

    private void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out Page? page))
        {
            page = key switch
            {
                "process" => new ProcessPage(_process),
                "config" => new ConfigPage(_config),
                "general" => new GeneralPage(_settings),
                "behavior" => new BehaviorPage(_behavior),
                "advanced" => new AdvancedPage(_advanced),
                "log" => new LogPage(_log),
                _ => new TuningPage(_tuning)
            };
            _pages[key] = page;
        }

        if (!ReferenceEquals(ContentFrame.Content, page))
            ContentFrame.Content = page;
    }

    // ==================== 生命周期 ====================

    /// <summary>
    /// 窗口隐藏 / 最小化时挂起进程采样。
    /// 这一条是「后台零占用」的硬闸门：不依赖任何页面是否正确释放引用计数，
    /// 只要窗口看不见，采样线程就必须睡下去。
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // WindowActivationState 只有 Activated / Deactivated / PointerActivated / CodeActivated，
        // 没有「最小化」这一档 —— 最小化在 WinUI 里表现为 Deactivated。
        _process.SetHostVisible(args.WindowActivationState != WindowActivationState.Deactivated);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 退出前把待写队列刷到磁盘，否则最后几条（往往是错误现场）会丢。
        _watcher.SetHostVisible(false);
        _logger.Flush();
    }

    private static string DescribeVersion()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null ? "v2.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }
}
