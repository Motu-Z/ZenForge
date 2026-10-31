using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 配置页：场景自动切换、总体配置方案、规则库概览。
///
/// 【为什么直接暴露 <see cref="ProfileService.Items"/>】
/// 那份集合本身就是 ObservableCollection 且由服务在增删时维护，
/// 这里再复制一份只会出现「服务改了、界面没改」。ViewModel 的职责是
/// 翻译意图（点哪张卡 = 应用哪个方案），不是重新持有一份数据。
/// </summary>
public sealed partial class ConfigViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly ScenarioService _scenarios;
    private readonly IBindingStore _bindings;
    private readonly ITuningStore _tuning;
    private readonly ILogService _log;

    private bool _loading;

    public ConfigViewModel(
        ProfileService profiles,
        ScenarioService scenarios,
        IBindingStore bindings,
        ITuningStore tuning,
        ILogService log)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        ArgumentNullException.ThrowIfNull(scenarios);
        _scenarios = scenarios;
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;

        Profiles = profiles.Items;
        ScenarioRules = scenarios.Rules;
    }

    public ObservableCollection<ProfileSnapshot> Profiles { get; }

    public ObservableCollection<ScenarioRule> ScenarioRules { get; }

    /// <summary>调度规则 / 调校规则的概览文本。用字符串而不是行模型：只读展示，没有交互。</summary>
    public ObservableCollection<string> ScheduleRules { get; } = new();

    public ObservableCollection<string> TuningRules { get; } = new();

    [ObservableProperty]
    public partial bool ScenarioEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasProfiles { get; set; }

    [ObservableProperty]
    public partial bool HasScheduleRules { get; set; }

    [ObservableProperty]
    public partial string ActiveProfileName { get; set; } = "未选择";

    /// <summary>页面每次进入时重算概览：方案、规则都可能刚在别的页被改过。</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            ScenarioEnabled = _scenarios.Enabled;
        }
        finally
        {
            _loading = false;
        }

        HasProfiles = Profiles.Count > 0;

        ProfileSnapshot? active = _profiles.ActiveId is { } id ? _profiles.FindById(id) : null;
        ActiveProfileName = active?.Name ?? "未选择";

        RebuildRuleLists();
    }

    partial void OnScenarioEnabledChanged(bool value)
    {
        if (_loading)
            return;

        _scenarios.SetEnabled(value);
        _log.Info($"配置页：场景自动切换 {(value ? "已开启" : "已关闭")}");
    }

    [RelayCommand]
    private void ApplyProfile(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        bool ok = _profiles.Apply(id);
        if (!ok)
            _log.Warn($"配置页：方案 {id} 应用失败");

        Refresh();
    }

    private void RebuildRuleLists()
    {
        TuningRules.Clear();
        ScheduleRules.Clear();

        TuningProfile profile = _tuning.Profile;

        int negative = 0;
        long sum = 0;
        foreach (int margin in profile.CurveOptimizer)
        {
            if (margin == 0)
                continue;

            negative++;
            sum += margin;
        }

        TuningRules.Add(profile.CurveOptimizer.Length == 0
            ? "曲线优化：未设置"
            : $"曲线优化：{negative}/{profile.CurveOptimizer.Length} 核已偏移，均值 {(negative == 0 ? 0 : sum / negative)}");

        TuningRules.Add(profile.FMaxMHz == 0 ? "FMax：未设置" : $"FMax：{profile.FMaxMHz} MHz");
        TuningRules.Add($"场景规则：{_scenarios.Rules.Count} 条");

        foreach (ScenarioRule rule in _scenarios.Rules)
            TuningRules.Add($"{rule.TriggerText} → {rule.ProfileName}");

        foreach (BindingRule rule in _bindings.Profile.Rules)
        {
            if (rule.Target == BindingTarget.None)
                continue;

            string target = rule.Target == BindingTarget.Custom
                ? (string.IsNullOrEmpty(rule.MaskName) ? "自定义掩码" : rule.MaskName)
                : rule.Target.ToString();

            ScheduleRules.Add($"{rule.Name} → {target}");
        }

        HasScheduleRules = ScheduleRules.Count > 0;
    }
}
