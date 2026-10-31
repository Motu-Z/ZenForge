using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Zentwik.App.Services;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.App;

public sealed partial class MainWindow : Window
{
    // x:Bind 的 OneTime 绑定在 InitializeComponent 期间求值，
    // 所以这些值必须在调用它之前算好。
    public string ConfigRoot { get; }
    public string LogRoot { get; }
    public string DispatcherNote { get; }
    public string BackdropMessage { get; }
    public InfoBarSeverity BackdropSeverity { get; }

    public MainWindow()
    {
        IAppPaths paths = AppHost.Services.GetRequiredService<IAppPaths>();
        IUiDispatcher dispatcher = AppHost.Dispatcher;
        ILogService log = AppHost.Services.GetRequiredService<ILogService>();

        ConfigRoot = $"配置目录：{paths.Root}";
        LogRoot = $"日志目录：{paths.Logs}";

        // 挂载真实调度器之后 IsCurrentThread 才为 true。
        // 这一行同时是「UI 线程同步上下文确实可用」的活体检测。
        DispatcherNote = dispatcher.IsCurrentThread
            ? "调度器：已挂载，当前线程即 UI 线程。"
            : "调度器：未挂载或不在 UI 线程 —— 检查 App.OnLaunched。";

        (BackdropMessage, BackdropSeverity) = ProbeBackdrop();

        InitializeComponent();

        AppWindow.Title = "Zentwik";
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        // 标题栏画在自己内容里，系统按钮仍由 AppWindow 管。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyBackdrop();
        ApplyCaptionColors();

        RootGrid.ActualThemeChanged += (_, _) => ApplyCaptionColors();

        log.Info($"主窗口已创建：{BackdropMessage}");
    }

    /// <summary>
    /// 材质探测。Mica 只在 Win11 22000+ 且有 DWM 合成时可用；
    /// 直接 new MicaBackdrop() 在不支持的机器上不会抛异常，但会得到一块死板的纯色，
    /// 所以必须先用 controller 的静态探测方法问一句。
    /// </summary>
    private static (string Message, InfoBarSeverity Severity) ProbeBackdrop()
    {
        if (MicaController.IsSupported())
            return ("Mica 已启用（跟随桌面壁纸取样）。", InfoBarSeverity.Success);

        if (DesktopAcrylicController.IsSupported())
            return ("Mica 不可用，已降级为 Acrylic。", InfoBarSeverity.Warning);

        return ("当前系统不支持系统背景材质，使用纯色背景。", InfoBarSeverity.Informational);
    }

    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        else if (DesktopAcrylicController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();
    }

    /// <summary>
    /// 标题栏按钮配色。
    ///
    /// 这三个颜色不接受 ThemeResource —— 它们走的是 AppWindow.TitleBar 的
    /// 非 XAML 属性通道。因此必须显式跟随主题切换，否则浅色主题下
    /// 会留下白色按钮压在浅色 Mica 上（看起来像「按钮消失了」）。
    /// </summary>
    private void ApplyCaptionColors()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;

        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 160, 160, 160)
            : Windows.UI.Color.FromArgb(255, 96, 96, 96);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
            : Windows.UI.Color.FromArgb(16, 0, 0, 0);
    }
}
