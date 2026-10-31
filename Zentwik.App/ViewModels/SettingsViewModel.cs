using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.App.Services;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 通用页：视觉设置 + 诊断 + 关于。
///
/// 【为什么材质与动效是两个独立属性，而不是一个「三档模式」】
/// 「极简 / 平衡 / 极致」把两件正交的事捆在一起：想要亚克力但不要动画，
/// 或者要 Mica 但要完整动效，三档里根本没有对应位置。拆成
/// <see cref="BackdropIndex"/>（材质）与 <see cref="MotionIndex"/>（动效）之后，
/// 每个维度各自单调，组合数从 3 变成 3×3，实现只是两条独立的写路径。
///
/// 【为什么用索引而不是枚举绑到下拉框】
/// ComboBox 的 SelectedIndex 是 int，直接绑 int 就不需要枚举↔字符串转换器；
/// 选项顺序与枚举声明顺序一一对应（Mica/Acrylic/Solid、Full/Reduced/Off），
/// 越界时统一夹紧，配置文件被手改成任何值都不会让界面停在未选中态。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeController _theme;
    private readonly IUiSettingsStore _settings;
    private readonly ILogService _log;
    private readonly SmuDiagnostics _diagnostics;
    private readonly IAppPaths _paths;

    /// <summary>初始化期回填选项时不为 true，否则会把「读到的值」当成「用户改的值」再写一遍。</summary>
    private bool _loading;

    /// <summary>材质降级回写界面时挡住自激：回写会再进一次 OnBackdropIndexChanged。</summary>
    private bool _applying;

    public SettingsViewModel(
        ThemeController theme,
        IUiSettingsStore settings,
        ILogService log,
        SmuDiagnostics diagnostics,
        IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;

        // 顺序与 AppThemeMode 的声明顺序一致：System / Light / Dark。
        ThemeOptions.Add("跟随系统");
        ThemeOptions.Add("浅色");
        ThemeOptions.Add("深色");

        BackdropOptions.Add("Mica（云母）");
        BackdropOptions.Add("Acrylic（亚克力）");
        BackdropOptions.Add("纯色");

        MotionOptions.Add("完整");
        MotionOptions.Add("精简");
        MotionOptions.Add("关闭");

        AccentOptions.Add("Zentwik 蓝");
        AccentOptions.Add("Windows 系统色");

        AboutText = $"版本 {AppVersion}";
    }

    // ==================== 样式 ====================

    public ObservableCollection<string> ThemeOptions { get; } = new();

    public ObservableCollection<string> BackdropOptions { get; } = new();

    public ObservableCollection<string> MotionOptions { get; } = new();

    public ObservableCollection<string> AccentOptions { get; } = new();

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial int BackdropIndex { get; set; }

    [ObservableProperty]
    public partial int MotionIndex { get; set; }

    [ObservableProperty]
    public partial int AccentIndex { get; set; }

    /// <summary>把已生效的值回填到界面。必须在窗口 Attach 之后调用。</summary>
    public void Initialize()
    {
        _loading = true;
        try
        {
            ThemeIndex = (int)_settings.Settings.ThemeMode;
            BackdropIndex = (int)_theme.CurrentBackdrop;
            MotionIndex = (int)_theme.CurrentMotion;
            AccentIndex = _settings.Settings.FollowSystemAccent ? 1 : 0;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading)
            return;

        _theme.ApplyTheme((AppThemeMode)Math.Clamp(value, 0, 2));
        _settings.Save();
    }

    partial void OnBackdropIndexChanged(int value)
    {
        if (_loading || _applying)
            return;

        // 实际生效的档位可能被降级（系统不支持 Mica），所以用返回值回写界面，
        // 让用户看到的是「真的用上了什么」，而不是「他点了什么」。
        _applying = true;
        try
        {
            BackdropKind applied = _theme.ApplyBackdrop((BackdropKind)Math.Clamp(value, 0, 2));
            _settings.Save();
            BackdropIndex = (int)applied;
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnMotionIndexChanged(int value)
    {
        if (_loading)
            return;

        _theme.ApplyMotion((MotionLevel)Math.Clamp(value, 0, 2));
        _settings.Save();
    }

    partial void OnAccentIndexChanged(int value)
    {
        if (_loading)
            return;

        // 索引 0 = 产品蓝（写死出厂色），1 = 跟系统走。
        _theme.ApplyAccent(value == 1, UiSettings.DefaultAccentHex);
        _settings.Save();
    }

    // ==================== 诊断 ====================

    [ObservableProperty]
    public partial string DiagnosticsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDiagnosticsVisible { get; set; }

    /// <summary>
    /// SMU 自检。<c>SmuDiagnostics.Run()</c> 会同步跑十几项探测（含一次 SMU 往返），
    /// 放在 UI 线程上会卡住几十毫秒到数百毫秒，所以整体丢到线程池，
    /// 结果再回 UI 线程 —— ObservableProperty 的 setter 会自己封送。
    /// </summary>
    [RelayCommand]
    private async Task RunSelfCheckAsync()
    {
        DiagnosticsText = "正在自检……";
        IsDiagnosticsVisible = true;

        string report;
        try
        {
            report = await Task.Run(() => _diagnostics.Run()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            report = $"自检失败：{ex.Message}";
            _log.Error($"SMU 自检异常：{ex}");
        }

        DiagnosticsText = report;
    }

    /// <summary>用资源管理器打开数据目录。UseShellExecute 交给 shell 打开指定路径。</summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.Root}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            _log.Error($"打开数据目录失败：{ex.Message}");
        }
    }

    // ==================== 关于 ====================

    public string AppVersion { get; } =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string AboutText { get; }

    public Uri GitHubUrl { get; } = new("https://github.com/zentwik/Zentwik");

    public string LicenseText => "MIT License";
}
