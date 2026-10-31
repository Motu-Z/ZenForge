using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 行为页：启动、后台、通知。
///
/// 每一项都是「改完立刻落盘」，不做保存按钮：这些开关没有中间态，
/// 用户拨了一下却发现没生效（忘了保存）比拨错更难受。
///
/// 唯一有真实副作用的是开机自启 —— 它改的是计划任务，可能被系统策略拒绝，
/// 所以 <see cref="AutoStart"/> 的 setter 以计划任务的真实返回值为准，
/// 失败时把界面拨回去，而不是把一个假的开启状态留在界面上。
/// </summary>
public sealed partial class BehaviorViewModel : ObservableObject
{
    private readonly IUiSettingsStore _settings;
    private readonly AutoStartService _autoStart;
    private readonly ILogService _log;

    /// <summary>初始化回填时不写盘、不触发副作用。</summary>
    private bool _loading;

    public BehaviorViewModel(IUiSettingsStore settings, AutoStartService autoStart, ILogService log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        ArgumentNullException.ThrowIfNull(autoStart);
        _autoStart = autoStart;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // ==================== 启动 ====================

    [ObservableProperty]
    public partial bool AutoStart { get; set; }

    [ObservableProperty]
    public partial bool ApplyTuningOnStartup { get; set; }

    // ==================== 后台 ====================

    /// <summary>总闸。关掉时下方三个子项整体禁用。</summary>
    [ObservableProperty]
    public partial bool BackendService { get; set; }

    [ObservableProperty]
    public partial bool WatchProcessBinding { get; set; }

    [ObservableProperty]
    public partial bool LightMode { get; set; }

    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; }

    // ==================== 通知 ====================

    [ObservableProperty]
    public partial bool NotifyOnError { get; set; }

    public void Initialize()
    {
        _loading = true;
        try
        {
            UiSettings s = _settings.Settings;

            // 自启状态以计划任务是否存在为准：ui.json 里的 AutoStart 只是缓存，
            // 用户可能在任务计划程序里手工删过。
            AutoStart = _autoStart.IsEnabled();
            ApplyTuningOnStartup = s.ApplyTuningOnStartup;
            BackendService = s.BackendService;
            WatchProcessBinding = s.WatchProcessBinding;
            LightMode = s.LightMode;
            ShowTrayIcon = s.ShowTrayIcon;
            NotifyOnError = s.NotifyOnError;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_loading)
            return;

        bool ok = value ? _autoStart.Enable() : _autoStart.Disable();

        // 回写真实结果。写入失败时 value 与真实状态不一致，这里纠正界面。
        if (ok != value)
        {
            _log.Warn($"开机自启切换失败：期望 {value}，实际 {ok}");
            _loading = true;
            try
            {
                AutoStart = ok;
            }
            finally
            {
                _loading = false;
            }
            return;
        }

        _settings.Settings.AutoStart = value;
        _settings.Save();
    }

    partial void OnApplyTuningOnStartupChanged(bool value)
    {
        if (_loading)
            return;

        _settings.Settings.ApplyTuningOnStartup = value;
        _settings.Save();
    }

    partial void OnBackendServiceChanged(bool value)
    {
        if (_loading)
            return;

        _settings.Settings.BackendService = value;
        _settings.Save();
    }

    partial void OnWatchProcessBindingChanged(bool value)
    {
        if (_loading)
            return;

        _settings.Settings.WatchProcessBinding = value;
        _settings.Save();
    }

    partial void OnLightModeChanged(bool value)
    {
        if (_loading)
            return;

        _settings.Settings.LightMode = value;
        _settings.Save();
    }

    partial void OnShowTrayIconChanged(bool value)
    {
        if (_loading)
            return;

        _settings.Settings.ShowTrayIcon = value;
        _settings.Save();
    }

    partial void OnNotifyOnErrorChanged(bool value)
    {
        if (_loading)
            return;

        _settings.Settings.NotifyOnError = value;
        _settings.Save();
    }
}
