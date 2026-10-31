using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Zentwik.Core.Services;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 进程页的一行。掩码 / 优先级两个下拉框是「即时生效」的：
/// 改完立刻落库并对当前已在运行的同类进程下发一次，不需要再点保存。
///
/// 【为什么不做「行内草稿 + 批量保存」】
/// 这个页面的用途是「我现在就想让这个程序跑到 CCD0 去」。
/// 草稿态会让用户改完下拉框、切走页面、然后发现没生效 —— 这是这类工具最常见的差评来源。
/// </summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    private readonly ProcessViewModel _owner;
    private bool _suppress;

    public ProcessRowViewModel(ProcessViewModel owner, int pid, string name, string path, double cpu, int priorityClass)
    {
        _owner = owner;
        Pid = pid;
        Name = name;
        PathText = path;
        Cpu = cpu;
        PriorityClass = priorityClass;
    }

    public int Pid { get; }

    public string Name { get; }

    /// <summary>可执行文件完整路径。取不到时为空串（受保护进程），列表按「—」显示。</summary>
    public string PathText { get; }

    /// <summary>进程基础优先级类（Toolhelp 快照自带，只读展示）。</summary>
    public int PriorityClass { get; private set; }

    public string PidText => Pid.ToString();

    // 下拉框的可选项由父级统一持有（掩码列表变化时只重建一份），
    // 行上只做转发：ItemsRepeater 的模板绑定的是行对象，拿不到页面级的集合。
    public ObservableCollection<string> TargetOptions => _owner.TargetOptions;

    public ObservableCollection<string> PriorityOptions => _owner.PriorityOptions;

    [ObservableProperty]
    public partial double Cpu { get; set; }

    public string CpuText => Cpu <= 0 ? "—" : $"{Cpu:0.0}%";

    /// <summary>进程名 → 当前生效规则的短标签，让用户一眼看到「这个进程被绑到哪了」。</summary>
    [ObservableProperty]
    public partial string RuleLabel { get; set; } = "未绑定";

    [ObservableProperty]
    public partial bool HasRule { get; set; }

    /// <summary>掩码下拉的选中索引。0 = 不绑定。</summary>
    [ObservableProperty]
    public partial int TargetIndex { get; set; }

    /// <summary>优先级下拉的选中索引。0 = 不设置。</summary>
    [ObservableProperty]
    public partial int PriorityIndex { get; set; }

    /// <summary>
    /// 采样线程每帧都会调到每一行。CPU 占用是滑动窗口均值，两位小数内的抖动
    /// 肉眼不可见，却会让 TextBlock 每秒重排 400 次 —— 只在有效变化时才写属性。
    /// </summary>
    internal void Update(double cpu, int priorityClass)
    {
        if (Math.Abs(cpu - Cpu) >= 0.05)
            Cpu = cpu;

        PriorityClass = priorityClass;
    }

    /// <summary>由父级在重建规则表后回填，避免触发一次写盘。</summary>
    internal void SetRuleState(int targetIndex, int priorityIndex, string label, bool hasRule)
    {
        _suppress = true;
        try
        {
            TargetIndex = targetIndex;
            PriorityIndex = priorityIndex;
            RuleLabel = label;
            HasRule = hasRule;
        }
        finally
        {
            _suppress = false;
        }
    }

    partial void OnCpuChanged(double value) => OnPropertyChanged(nameof(CpuText));

    partial void OnTargetIndexChanged(int value)
    {
        if (_suppress)
            return;
        _owner.ApplyRowEdit(this, value, PriorityIndex);
    }

    partial void OnPriorityIndexChanged(int value)
    {
        if (_suppress)
            return;
        _owner.ApplyRowEdit(this, TargetIndex, value);
    }
}
