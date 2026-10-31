using System;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.App.Services;

/// <summary>窗口材质。纯色用于「材质被系统策略禁用」或用户偏好固态底的情况。</summary>
public enum BackdropKind
{
    Mica,
    Acrylic,
    Solid
}

/// <summary>窗口动效强度。</summary>
public enum MotionLevel
{
    Full,
    Reduced,
    Off
}

/// <summary>
/// 主题控制：明暗模式、产品强调色、窗口材质、窗口动效、标题栏按钮配色。
///
/// 【职责边界】
/// 这里只做「把 UiSettings 翻译成 WinUI 资源 / 系统对象」这一件事，不持有页面状态。
/// 页面不需要知道当前是深色还是浅色 —— 它们只引用 ThemeResource，由 WinUI 自己重算。
///
/// 【强调色为什么要覆盖资源键，而不是给每个控件设属性】
/// 强调色会出现在按钮描边、下拉框焦点态、滑块、开关开启态、列表选中项……
/// 逐个控件设属性既漏又散；替换 <c>AppAccentBrush</c> 这一组键，全局一次性生效。
/// 覆盖写在 Application.Resources 的**根级**：根级键优先于 ThemeDictionaries 被查到，
/// 所以一次覆盖同时命中明暗两套主题。
///
/// 【材质与动效为什么拆成两个独立维度】
/// 「极简 / 平衡 / 极致」这种三档绑定把两件正交的事捆在一起：想要亚克力但不想有动画，
/// 或想要 Mica 但需要完整动效，都表达不出来。拆开之后每个维度各自单调，
/// 组合数从 3 变成 3×3，而实现只是两个独立的枚举与两个独立的应用函数。
/// </summary>
public sealed class ThemeController
{
    private readonly IUiSettingsStore _settings;
    private readonly ILogService _log;
    private readonly UISettings _systemUi = new();

    private Window? _window;

    /// <summary>材质不可用时基底层必须不透明，否则半透明的面会让桌面从窗口里透出来。</summary>
    private bool _baseFillOpaque;

    public ThemeController(IUiSettingsStore settings, ILogService log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>当前生效的明暗模式（已把「跟随系统」解析成具体值）。</summary>
    public ElementTheme CurrentTheme { get; private set; } = ElementTheme.Dark;

    /// <summary>当前生效的强调色。</summary>
    public Windows.UI.Color AccentColor { get; private set; } = Windows.UI.Color.FromArgb(255, 0x38, 0xBD, 0xF8);

    /// <summary>当前生效的窗口材质（已把不支持的档位降级后的结果）。</summary>
    public BackdropKind CurrentBackdrop { get; private set; } = BackdropKind.Mica;

    /// <summary>当前生效的动效强度。</summary>
    public MotionLevel CurrentMotion { get; private set; } = MotionLevel.Full;

    /// <summary>
    /// 启动期调用一次，必须在创建任何 Window / Page 之前。
    /// </summary>
    public void Initialize()
    {
        _settings.Load();
        UiSettings settings = _settings.Settings;

        AccentColor = ResolveAccent(settings);
        WriteAccent(AccentColor);

        CurrentTheme = settings.ThemeMode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => SystemTheme()
        };

        CurrentBackdrop = ParseBackdrop(settings.Backdrop);
        CurrentMotion = ParseMotion(settings.Motion);

        _log.Info($"主题初始化：{DescribeTheme(CurrentTheme)}，强调色 #{AccentColor.R:X2}{AccentColor.G:X2}{AccentColor.B:X2}，" +
                  $"材质 {CurrentBackdrop}，动效 {CurrentMotion}");
    }

    /// <summary>
    /// 绑定窗口：应用主题、材质、动效与标题栏配色，并在主题切换时同步。
    /// </summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;

        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = CurrentTheme;
            // 跟随系统模式下，用户在 Windows 设置里切换明暗不会重建窗口，
            // 只能靠 ActualThemeChanged 捕获 —— 标题栏按钮走的是 AppWindow 通道，
            // 不接这个事件就会留下「浅色底 + 白色按钮」的隐形按钮。
            root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        }

        ApplyBackdrop(CurrentBackdrop);
        ApplyMotion(CurrentMotion);
        ApplyCaptionColors();
    }

    /// <summary>运行期切换主题（通用页的「主题模式」下拉直接用这个）。</summary>
    public void ApplyTheme(AppThemeMode mode)
    {
        CurrentTheme = mode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => SystemTheme()
        };

        _settings.Settings.ThemeMode = mode;

        if (_window?.Content is FrameworkElement root)
            root.RequestedTheme = CurrentTheme;

        ApplyCaptionColors();
    }

    /// <summary>
    /// 运行期切换强调色。
    ///
    /// <paramref name="followSystem"/> 为 true 时忽略 <paramref name="hex"/>，直接取系统强调色；
    /// 为 false 时用 <paramref name="hex"/>，解析失败回落产品色。
    ///
    /// 必须同时写 <c>FollowSystemAccentExplicit</c>：它为 false 时 <see cref="ResolveAccent"/>
    /// 会强制走产品色，下次启动用户选的色就白丢了。
    /// </summary>
    public void ApplyAccent(bool followSystem, string? hex)
    {
        _settings.Settings.FollowSystemAccent = followSystem;
        _settings.Settings.FollowSystemAccentExplicit = true;

        if (!followSystem && !string.IsNullOrWhiteSpace(hex))
            _settings.Settings.CustomAccentHex = hex!;

        AccentColor = ResolveAccent(_settings.Settings);
        WriteAccent(AccentColor);
    }

    // ==================== 材质 ====================

    /// <summary>
    /// 切换窗口材质。
    ///
    /// 必须先用 Controller 的静态探测方法问一句再 new：在不支持的系统上
    /// <c>new MicaBackdrop()</c> 不抛异常，只给一块死板的纯色，看起来像「材质坏了」。
    /// 所以这里做的是 [请求档位 → 可行性探测 → 实际应用] 三步，返回值是**实际生效**的档位。
    /// </summary>
    public BackdropKind ApplyBackdrop(BackdropKind requested)
    {
        if (_window is null)
            return requested;

        // 逐级降级：Mica 不可用就退 Acrylic，Acrylic 也不可用就纯色。
        BackdropKind resolved = requested;
        if (resolved == BackdropKind.Mica && !MicaController.IsSupported())
            resolved = DesktopAcrylicController.IsSupported() ? BackdropKind.Acrylic : BackdropKind.Solid;
        else if (resolved == BackdropKind.Acrylic && !DesktopAcrylicController.IsSupported())
            resolved = BackdropKind.Solid;

        _window.SystemBackdrop = resolved switch
        {
            BackdropKind.Mica => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            BackdropKind.Acrylic => new DesktopAcrylicBackdrop(),
            _ => null
        };

        if (resolved != requested)
            _log.Warn($"材质 {requested} 不受支持，已降级为 {resolved}");

        CurrentBackdrop = resolved;

        // 纯色档 = 没有材质可用。此时三层半透明面直接压在桌面上，
        // 桌面的文字会从窗口里清晰透出来 —— 必须在最底下补一层不透明底。
        WriteBaseFill(!(resolved == BackdropKind.Mica || resolved == BackdropKind.Acrylic));

        _settings.Settings.Backdrop = resolved.ToString();
        return resolved;
    }

    /// <summary>
    /// 切换窗口动效。
    ///
    /// WinUI 3 没有「全局关动画」的开关，能做到的是
    /// <c>Timeline.AllowDependentAnimations</c> —— 它只作用于 ThemeTransition
    /// 与隐式动画这一类「依赖型」动画，这正是我们界面里全部会动的东西。
    /// 系统级的窗口最小化 / Snap 动画归 DWM，应用无权关闭。
    ///
    /// 「精简」与「关闭」目前落到同一实现（都关依赖型动画）：差别只有在接入
    /// 自绘 Composition 动画之后才有意义 —— 那时精简保留淡入、关闭什么都不放。
    /// </summary>
    public void ApplyMotion(MotionLevel level)
    {
        Timeline.AllowDependentAnimations = level == MotionLevel.Full;

        CurrentMotion = level;
        _settings.Settings.Motion = level.ToString();
    }

    // ==================== 标题栏 ====================

    /// <summary>
    /// 标题栏按钮的配色。
    ///
    /// 这些颜色**不能**用 ThemeResource：它们走 <c>AppWindow.TitleBar</c> 的非 XAML 通道，
    /// 传进去的是 Windows.UI.Color 值，不参与 XAML 的资源求值。
    /// 所以必须在每次主题变化时手工重设。
    ///
    /// 三个按钮本身是自绘的（46x32，关闭悬停变红），这里的配色是为了让系统在
    /// 我们没画到的位置（如 Snap Layouts 悬停区）不冒出白底。
    /// </summary>
    /// <summary>
    /// 标题栏系统按钮配色。
    ///
    /// 三个窗口控制按钮已由 MainWindow 自绘（悬浮 12px / 圆角 6px），系统的
    /// min/max/close 命中区域也在接管时被 InputNonClientPointerSource 清空。
    /// 这里把系统按钮整体染成全透明且**不再随主题变化** —— 任何可见色都会在
    /// 自绘按钮后面叠出一排「幽灵按钮」。
    /// </summary>
    public void ApplyCaptionColors()
    {
        if (_window is null)
            return;

        _window.AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonForegroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonPressedBackgroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.Transparent;
        _window.AppWindow.TitleBar.ButtonPressedForegroundColor = Colors.Transparent;
    }

    /// <summary>
    /// 基底层填充。材质可用时透明，让 Mica / Acrylic 透上来；
    /// 不可用时铺不透明底，避免「窗口是透明的，桌面从里面透出来」。
    /// </summary>
    private void WriteBaseFill(bool opaque)
    {
        _baseFillOpaque = opaque;

        if (!opaque)
        {
            Application.Current.Resources["AppWindowBaseBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }

        bool dark = (_window?.Content as FrameworkElement)?.ActualTheme != ElementTheme.Light;
        Application.Current.Resources["AppWindowBaseBrush"] = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 0x12, 0x12, 0x14)
            : Windows.UI.Color.FromArgb(255, 0xF3, 0xF4, 0xF6));
    }

    // ==================== 内部 ====================

    private static BackdropKind ParseBackdrop(string? raw) => raw switch
    {
        nameof(BackdropKind.Acrylic) => BackdropKind.Acrylic,
        nameof(BackdropKind.Solid) => BackdropKind.Solid,
        _ => BackdropKind.Mica
    };

    private static MotionLevel ParseMotion(string? raw) => raw switch
    {
        nameof(MotionLevel.Reduced) => MotionLevel.Reduced,
        nameof(MotionLevel.Off) => MotionLevel.Off,
        _ => MotionLevel.Full
    };

    private ElementTheme SystemTheme() =>
        _systemUi.GetColorValue(UIColorType.Background).R > 128 ? ElementTheme.Light : ElementTheme.Dark;

    private Windows.UI.Color ResolveAccent(UiSettings settings)
    {
        // 默认走产品强调色（#38BDF8），不是系统强调色。
        // 旧配置里 FollowSystemAccent 缺省为 true，但它并不代表「用户主动选了跟随系统」——
        // FollowSystemAccentExplicit 为 false 时一律用产品色，避免首次启动就被系统色染掉。
        if (!settings.FollowSystemAccentExplicit)
            return ParseHex(UiSettings.DefaultAccentHex);

        if (settings.FollowSystemAccent)
            return _systemUi.GetColorValue(UIColorType.Accent);

        return ParseHex(settings.CustomAccentHex);
    }

    private static Windows.UI.Color ParseHex(string? hex)
    {
        Windows.UI.Color fallback = Windows.UI.Color.FromArgb(255, 0x38, 0xBD, 0xF8);
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;

        string text = hex.TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint value))
            return fallback;

        return Windows.UI.Color.FromArgb(
            255,
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }

    /// <summary>
    /// 把强调色写进应用资源字典。
    ///
    /// 这一组键是一个整体：只改 Brush 不改 Soft/Border，会出现「按钮是青色、焦点框还是系统蓝」；
    /// 漏掉滑块的三个键，就会出现「按钮青、滑轨系统蓝」——都是同一个原因：
    /// 强调色的实际消费点分散在原生模板的主题键里，而不是集中在我们的控件上。
    /// </summary>
    private void WriteAccent(Windows.UI.Color color)
    {
        ResourceDictionary resources = Application.Current.Resources;

        resources["AppAccentBrush"] = new SolidColorBrush(color);
        resources["AppAccentHoverBrush"] = new SolidColorBrush(Lighten(color, 0.35));
        resources["AppAccentPressedBrush"] = new SolidColorBrush(Darken(color, 0.22));
        resources["AppAccentSoftBrush"] = new SolidColorBrush(WithAlpha(color, 0x2E));
        resources["AppAccentBorderBrush"] = new SolidColorBrush(WithAlpha(color, 0x8C));

        // 原生模板里的强调色消费点（滑块 / 开关），一并跟上。
        resources["SliderTrackValueFill"] = new SolidColorBrush(color);
        resources["SliderTrackValueFillPointerOver"] = new SolidColorBrush(Lighten(color, 0.35));
        resources["SliderTrackValueFillPressed"] = new SolidColorBrush(Darken(color, 0.22));
        resources["SliderThumbBackground"] = new SolidColorBrush(color);
        resources["SliderThumbBackgroundPointerOver"] = new SolidColorBrush(Lighten(color, 0.35));
        resources["SliderThumbBackgroundPressed"] = new SolidColorBrush(Darken(color, 0.22));
        resources["ToggleSwitchFillOn"] = new SolidColorBrush(color);
        resources["ToggleSwitchFillOnPointerOver"] = new SolidColorBrush(Lighten(color, 0.35));
        resources["ToggleSwitchFillOnPressed"] = new SolidColorBrush(Darken(color, 0.22));
        resources["ToggleSwitchStrokeOn"] = new SolidColorBrush(color);
        resources["ToggleSwitchStrokeOnPointerOver"] = new SolidColorBrush(Lighten(color, 0.35));
        resources["ToggleSwitchStrokeOnPressed"] = new SolidColorBrush(Darken(color, 0.22));
        resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(color);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Windows.UI.Color Lighten(Windows.UI.Color c, double amount) =>
        Windows.UI.Color.FromArgb(255,
            (byte)Math.Min(255, c.R + (255 - c.R) * amount),
            (byte)Math.Min(255, c.G + (255 - c.G) * amount),
            (byte)Math.Min(255, c.B + (255 - c.B) * amount));

    private static Windows.UI.Color Darken(Windows.UI.Color c, double amount) =>
        Windows.UI.Color.FromArgb(255,
            (byte)(c.R * (1 - amount)),
            (byte)(c.G * (1 - amount)),
            (byte)(c.B * (1 - amount)));

    private static string DescribeTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "浅色",
        ElementTheme.Dark => "深色",
        _ => "跟随系统"
    };
}
