using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 已保存规则的一行。禁用 / 应用 / 删除三个动作都在行内，且强制单行显示 ——
/// 换行会让规则列表在窗口变窄时高度跳变，一屏能看到的规则数随机。
/// </summary>
public sealed partial class RuleRowViewModel : ObservableObject
{
    private readonly ProcessViewModel _owner;
    private bool _suppress;

    public RuleRowViewModel(ProcessViewModel owner, string name, string targetLabel, string priorityLabel, bool enabled, int matchingProcesses)
    {
        _owner = owner;
        Name = name;
        TargetLabel = targetLabel;
        PriorityLabel = priorityLabel;
        // 构造期必须屏蔽回调：IsEnabled 的 setter 会把「启用」当成一次用户操作去写盘。
        _suppress = true;
        try
        {
            MatchingProcesses = matchingProcesses;
            IsEnabled = enabled;
        }
        finally
        {
            _suppress = false;
        }

        ApplyCommand = new RelayCommand(() => _owner.ApplyRuleNow(this));
        DeleteCommand = new RelayCommand(() => _owner.DeleteRule(this));
        ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
    }

    public string Name { get; }

    public string TargetLabel { get; }

    public string PriorityLabel { get; }

    /// <summary>当前正在运行的同名进程数。0 表示规则在等下一次启动。</summary>
    [ObservableProperty]
    public partial int MatchingProcesses { get; set; }

    public string MatchingText => MatchingProcesses > 0 ? $"运行中 {MatchingProcesses}" : "未运行";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    public partial bool IsEnabled { get; set; }

    public IRelayCommand ApplyCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand ToggleCommand { get; }

    /// <summary>供「启用」按钮显隐用：{x:Bind} 不能对布尔取反。</summary>
    public bool IsDisabled => !IsEnabled;

    internal void SetEnabledSilently(bool enabled)
    {
        _suppress = true;
        try
        {
            IsEnabled = enabled;
        }
        finally
        {
            _suppress = false;
        }
    }

    partial void OnMatchingProcessesChanged(int value) => OnPropertyChanged(nameof(MatchingText));

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress)
            return;
        _owner.SetRuleEnabled(this, value);
    }
}
