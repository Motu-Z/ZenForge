using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 高级页：安全操作与数据清除。
///
/// 这一页上的每个按钮都不做「点一下就完事」之外的承诺：
/// 真正需要二次确认的（清除全部数据）由 View 弹 ContentDialog，
/// ViewModel 只暴露 <see cref="ClearAllData"/> 这一个执行入口。
/// </summary>
public sealed partial class AdvancedViewModel : ObservableObject
{
    private readonly ISmuReader _smu;
    private readonly ITuningStore _tuning;
    private readonly TuningApplyService _apply;
    private readonly IBindingStore _bindings;
    private readonly IMaskStore _masks;
    private readonly IUiSettingsStore _settings;
    private readonly ProfileService _profiles;
    private readonly ScenarioService _scenarios;
    private readonly AutoStartService _autoStart;
    private readonly IAppPaths _paths;
    private readonly ILogService _log;

    public AdvancedViewModel(
        ISmuReader smu,
        ITuningStore tuning,
        TuningApplyService apply,
        IBindingStore bindings,
        IMaskStore masks,
        IUiSettingsStore settings,
        ProfileService profiles,
        ScenarioService scenarios,
        AutoStartService autoStart,
        IAppPaths paths,
        ILogService log)
    {
        ArgumentNullException.ThrowIfNull(smu);
        _smu = smu;
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
        ArgumentNullException.ThrowIfNull(apply);
        _apply = apply;
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        ArgumentNullException.ThrowIfNull(masks);
        _masks = masks;
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        ArgumentNullException.ThrowIfNull(scenarios);
        _scenarios = scenarios;
        ArgumentNullException.ThrowIfNull(autoStart);
        _autoStart = autoStart;
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>操作结果直接铺在页面上，不弹窗 —— 这几件事做完用户就想看到结论。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// 恢复出厂调校：CO 全部归零、FMax 回到出厂基线，并立即写回硬件。
    ///
    /// 只清配置文件是不够的 —— 用户点这个按钮的诉求是「现在就恢复正常」，
    /// 停留在「文件里是 0、硬件还压着 -30」只会让他以为按钮坏了。
    /// </summary>
    [RelayCommand]
    private async Task RestoreFactoryTuningAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            uint baseline = _tuning.ResetToFactory();
            TuningApplyOutcome outcome = await Task.Run(_apply.ApplyFromStore).ConfigureAwait(true);

            StatusText = outcome.Success
                ? $"已恢复出厂调校：CO 归零，FMax {baseline} MHz"
                : $"配置文件已重置，但写回硬件未完成：{outcome.Summary}";
            IsStatusVisible = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 停用全部功能：后台服务总闸关掉、场景自动切换关掉、开机自启注销。
    ///
    /// 只「停用」不「删除」—— 用户的规则、方案、调校值全都留着，
    /// 重新打开开关就能回到原样。
    /// </summary>
    [RelayCommand]
    private void DisableEverything()
    {
        _scenarios.SetEnabled(false);
        _autoStart.Disable();

        UiSettings s = _settings.Settings;
        s.BackendService = false;
        s.AutoStart = false;
        _settings.Save();

        StatusText = "已停用全部功能：后台服务、场景切换、开机自启均已关闭";
        IsStatusVisible = true;
        _log.Info("高级页：停用全部功能");
    }

    /// <summary>
    /// 清除全部数据。调用前必须由 View 弹确认框。
    ///
    /// 顺序是先清硬件再清文件：反过来的话中途失败会留下
    /// 「文件没了、硬件还挂着负压」这种最难解释的状态。
    /// </summary>
    public void ClearAllData()
    {
        _tuning.ResetToFactory();
        _apply.ApplyFromStore();

        _bindings.ResetToFactory();
        _masks.ResetToFactory();

        foreach (ProfileSnapshot profile in Snapshot(_profiles.Items))
            _profiles.Delete(profile.Id);

        foreach (ScenarioRule rule in Snapshot(_scenarios.Rules))
            _scenarios.Delete(rule.Id);

        _settings.ResetToFactory();

        StatusText = "已清除全部数据：调校、方案、规则、掩码、界面设置";
        IsStatusVisible = true;
        _log.Info("高级页：清除全部数据");
    }

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

    /// <summary>SMU 是否可用。硬件层没连通时，写回类按钮应当给出提示而不是假装成功。</summary>
    public bool IsSmuAvailable => _smu.IsAvailable;

    /// <summary>
    /// 删除会边遍历边改集合，先复制一份 ID 出来再删。
    /// 泛型方法而不是两个重载：两处集合元素类型不同，但「快照后删除」的逻辑是同一件事。
    /// </summary>
    private static List<T> Snapshot<T>(IEnumerable<T> source) => new(source);
}
