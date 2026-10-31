using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 进程页：运行中进程列表 + 已保存绑核规则。
///
/// 【线程模型（本页最容易踩的坑）】
/// <see cref="ProcessRefresher.Refreshed"/> 在**采样线程**（专用 STA 后台线程）上同步触发，
/// <see cref="IProcessWatcher.ProcessSetChanged"/> 在监听循环的线程上触发。
/// 两者都不是 UI 线程，直接改 <c>ObservableCollection</c> 会抛 STA 异常或让
/// ItemsRepeater 的回收逻辑读到半更新状态。因此所有入口先 <see cref="IUiDispatcher.Post"/>。
///
/// 【列表更新为什么是增量】
/// 每秒一帧全量快照，<c>Clear()</c> + 重填会让滚动位置每秒归零、选中态丢失，
/// 并在 ItemsRepeater 里制造大量元素重建。这里按 PID 做 diff：
/// 已有的只更新数值，新的插到排序位，消失的按索引移除。
/// </summary>
public sealed partial class ProcessViewModel : ObservableObject
{
    private readonly IProcessWatcher _watcher;
    private readonly ProcessRefresher _refresher;
    private readonly IBindingStore _bindings;
    private readonly IMaskStore _masks;
    private readonly ICpuSetBinder _binder;
    private readonly ILogService _log;
    private readonly IUiDispatcher _dispatcher;

    private readonly Dictionary<int, ProcessRowViewModel> _rowsByPid = new();

    /// <summary>
    /// 每帧复用同一个排序缓冲。每秒新建一个 List + 一个比较器是纯浪费，
    /// 而这块内存的生命周期与进程相同，复用没有正确性代价。
    /// </summary>
    private readonly List<ProcessRefresher.Entry> _mergeBuffer = new();

    /// <summary>每帧复用的「本帧存活 PID」集合，避免每秒新建一个 HashSet。</summary>
    private readonly HashSet<int> _alivePids = new();

    /// <summary>规则名 → 运行中实例数。每帧只遍历进程一次填这里，再分发给规则行。</summary>
    private readonly Dictionary<string, int> _runningByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CustomMask> _maskList = new();
    private bool _loaded;
    private bool _attached;

    public ProcessViewModel(
        IProcessWatcher watcher,
        ProcessRefresher refresher,
        IBindingStore bindings,
        IMaskStore masks,
        ICpuSetBinder binder,
        ILogService log,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        _watcher = watcher;
        ArgumentNullException.ThrowIfNull(refresher);
        _refresher = refresher;
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        ArgumentNullException.ThrowIfNull(masks);
        _masks = masks;
        ArgumentNullException.ThrowIfNull(binder);
        _binder = binder;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;

        TargetOptions.Add("不绑定");
        TargetOptions.Add("CCD0");
        TargetOptions.Add("CCD1");

        PriorityOptions.Add("不设置");
        foreach (string option in ProcessPriority.Options)
            PriorityOptions.Add(option);
    }

    // ==================== 下拉框数据源 ====================

    public ObservableCollection<string> TargetOptions { get; } = new();

    public ObservableCollection<string> PriorityOptions { get; } = new();

    // ==================== 列表 ====================

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

    public ObservableCollection<RuleRowViewModel> Rules { get; } = new();

    [ObservableProperty]
    public partial string ProcessCountText { get; set; } = "0 个进程";

    /// <summary>
    /// 列表是否为空。0 个进程时只留一个表头会显得卡片是坏的，
    /// 空态由这个属性驱动，而不是靠「表头下面碰巧没有行」。
    /// </summary>
    [ObservableProperty]
    public partial bool HasNoProcesses { get; set; } = true;

    [ObservableProperty]
    public partial string RulesCountText { get; set; } = "0";

    /// <summary>搜索过滤词。空串 = 不过滤。</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// 搜索框每次敲键都重算一次列表。
    ///
    /// 这里只请求一次采样，不自己遍历 —— 过滤跑在 MergeRows 里，
    /// 与采样同一条路径，避免出现「过滤后的集合」和「采样增量」两套真相。
    /// </summary>
    partial void OnSearchTextChanged(string value) => _refresher.RequestSample();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesEmpty))]
    public partial bool HasRules { get; set; }

    public bool IsRulesEmpty => !HasRules;

    // ==================== 监听状态 ====================

    [ObservableProperty]
    public partial bool IsWatcherRunning { get; set; }

    [ObservableProperty]
    public partial string WatcherText { get; set; } = "后台监听未启动";

    [ObservableProperty]
    public partial string WatcherButtonText { get; set; } = "启动监听";

    // ==================== 忙碌 / 提示 ====================

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial UiSeverity StatusLevel { get; set; } = UiSeverity.Info;

    // ==================== 命令 ====================

    [RelayCommand]
    private void ToggleWatcher()
    {
        if (_watcher.IsRunning)
        {
            _watcher.Stop();
        }
        else
        {
            _watcher.Start();
        }

        SyncWatcherState();
        SetStatus(UiSeverity.Info, "后台监听",
            _watcher.IsRunning
                ? "已启动：新出现的进程会按规则自动绑核。"
                : "已停止：仅保留已保存规则，不再自动绑核。");
    }

    /// <summary>按当前规则重扫一帧：改完规则想立刻看到结果时用。</summary>
    [RelayCommand]
    private void Reapply()
    {
        _watcher.ReapplyNow();
        _refresher.RequestSample();
        SetStatus(UiSeverity.Info, "已重扫", "已按当前规则重新判定一轮进程。");
    }

    // ==================== 生命周期 ====================

    /// <summary>页面加载：只做一次磁盘读取，之后靠采样线程喂数据。</summary>
    public void Attach()
    {
        if (!_loaded)
        {
            _masks.Load();
            _bindings.Load();
            RebuildMaskOptions();
            _loaded = true;
        }

        if (!_attached)
        {
            _refresher.Refreshed += OnRefreshed;
            _watcher.ProcessSetChanged += OnProcessSetChanged;
            _attached = true;
        }

        // 引用计数 +1：采样线程从「挂起」切到「按间隔采样」。
        _refresher.AcquireWatch();
        _refresher.RequestSample();

        SyncWatcherState();
        RebuildRules();
    }

    /// <summary>页面离开：释放引用计数，采样线程随即彻底挂起（不是降频）。</summary>
    public void Detach()
    {
        if (!_attached)
            return;

        _refresher.Refreshed -= OnRefreshed;
        _watcher.ProcessSetChanged -= OnProcessSetChanged;
        _attached = false;

        _refresher.ReleaseWatch();
    }

    /// <summary>
    /// 宿主窗口显隐。两个组件都要通知：
    /// <c>ProcessRefresher</c> 决定要不要每帧采样，
    /// <c>ProcessWatcher</c> 决定后台降级轮询要不要挂起。
    /// 少通知一个，收进托盘后仍会有 3% 左右的 CPU 占用。
    /// </summary>
    public void SetHostVisible(bool visible)
    {
        _refresher.SetHostVisible(visible);
        _watcher.SetHostVisible(visible);
    }

    // ==================== 采样回调 ====================

    private void OnRefreshed(IReadOnlyList<ProcessRefresher.Entry> entries)
    {
        // 采样线程 → UI 线程。这里必须 Post：集合改动要在 UI 线程做。
        _dispatcher.Post(() => MergeRows(entries));
    }

    private void OnProcessSetChanged()
    {
        _dispatcher.Post(() =>
        {
            SyncWatcherState();
            UpdateRuleMatchCounts();
        });
    }

    private void MergeRows(IReadOnlyList<ProcessRefresher.Entry> entries)
    {
        // 排序在复用缓冲上原地完成：CPU 占用列不参与排序，行位置就稳定，
        // 用户点开某个下拉框时它不会因为「这一帧别的进程变忙」而跳走。
        _mergeBuffer.Clear();
        _mergeBuffer.AddRange(entries);
        _mergeBuffer.Sort(CompareByName);

        _alivePids.Clear();
        int position = 0;

        string filter = SearchText.Trim();

        foreach (ProcessRefresher.Entry entry in _mergeBuffer)
        {
            // 过滤必须在登记 _alivePids 之前：被过滤掉的行若仍被当成「存活」，
            // 下面的清理循环就不会把它移出列表，清空搜索词后旧行会残留在列表里。
            if (filter.Length > 0
                && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            _alivePids.Add(entry.Pid);

            if (_rowsByPid.TryGetValue(entry.Pid, out ProcessRowViewModel? row))
            {
                row.Update(entry.Cpu, entry.PriorityClass);
            }
            else
            {
                row = new ProcessRowViewModel(this, entry.Pid, entry.Name, entry.Path, entry.Cpu, entry.PriorityClass);
                _rowsByPid[entry.Pid] = row;
                Processes.Insert(Math.Min(position, Processes.Count), row);
                ApplyRuleStateToRow(row);
            }

            position++;
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            int pid = Processes[i].Pid;
            if (_alivePids.Contains(pid))
                continue;

            _rowsByPid.Remove(pid);
            Processes.RemoveAt(i);
        }

        // 只在文案真的变了才赋值：文本相同也会触发一次 measure，
        // 每秒一次的无意义通知足以让列表头部持续参与布局。
        string countText = $"{Processes.Count} 个进程";
        if (!string.Equals(ProcessCountText, countText, StringComparison.Ordinal))
            ProcessCountText = countText;

        bool empty = Processes.Count == 0;
        if (HasNoProcesses != empty)
            HasNoProcesses = empty;

        UpdateRuleMatchCounts();
    }

    private static int CompareByName(ProcessRefresher.Entry a, ProcessRefresher.Entry b)
    {
        int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : a.Pid.CompareTo(b.Pid);
    }

    // ==================== 行内编辑 ====================

    /// <summary>
    /// 下拉框改动的唯一出口：立刻落库 → 立刻对当前运行中的同名进程下发。
    ///
    /// 「立刻下发」是有意的：用户改下拉框的语境是「这个程序现在就要换个 CCD」，
    /// 让他再去找一个「应用」按钮，等于把一次操作拆成两次。
    /// </summary>
    public void ApplyRowEdit(ProcessRowViewModel row, int targetIndex, int priorityIndex)
    {
        bool bothNone = targetIndex <= 0 && priorityIndex <= 0;

        List<BindingRule> rules = _bindings.Profile.Rules.ToList();
        BindingRule? rule = rules.FirstOrDefault(r => string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase));

        if (bothNone)
        {
            if (rule is not null)
            {
                rules.Remove(rule);
                _bindings.Save(rules);
                _log.Info($"已移除规则：{row.Name}");
            }

            row.SetRuleState(0, 0, "未绑定", false);
            RebuildRules();
            return;
        }

        if (rule is null)
        {
            rule = new BindingRule { Name = row.Name };
            rules.Add(rule);
        }

        (BindingTarget target, string maskId, ulong mask, string maskName) = ResolveTarget(targetIndex);
        rule.Target = target;
        rule.MaskId = maskId;
        rule.Mask = mask;
        rule.MaskName = maskName;
        rule.Priority = priorityIndex > 0 && priorityIndex <= ProcessPriority.Options.Length
            ? ProcessPriority.Options[priorityIndex - 1]
            : string.Empty;
        rule.Enabled = true;

        // 规则按进程名记录，因此当前所有同名实例的展示标签要一起变。
        _bindings.Save(rules);

        foreach (ProcessRowViewModel sibling in Processes.Where(p =>
                     string.Equals(p.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
        {
            sibling.SetRuleState(targetIndex, priorityIndex, rule.Label, true);
        }

        int applied = ApplyToRunning(row.Name, targetIndex, priorityIndex);
        RebuildRules();

        SetStatus(UiSeverity.Success, "规则已保存",
            applied > 0
                ? $"{row.Name} → {rule.Label}，已对 {applied} 个运行中实例生效。"
                : $"{row.Name} → {rule.Label}，当前没有运行中的实例，下次启动时生效。");
    }

    // ==================== 规则行操作 ====================

    public void ApplyRuleNow(RuleRowViewModel ruleRow)
    {
        BindingRule? rule = _bindings.Profile.Rules
            .FirstOrDefault(r => string.Equals(r.Name, ruleRow.Name, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
            return;

        int targetIndex = TargetIndexOf(rule);
        int priorityIndex = PriorityIndexOf(rule);
        int applied = ApplyToRunning(rule.Name, targetIndex, priorityIndex);

        SetStatus(applied > 0 ? UiSeverity.Success : UiSeverity.Warning, "应用规则",
            applied > 0 ? $"{rule.Name} 已对 {applied} 个实例生效。" : $"{rule.Name} 当前没有运行中的实例。");
    }

    public void DeleteRule(RuleRowViewModel ruleRow)
    {
        List<BindingRule> rules = _bindings.Profile.Rules
            .Where(r => !string.Equals(r.Name, ruleRow.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _bindings.Save(rules);

        foreach (ProcessRowViewModel row in Processes.Where(p =>
                     string.Equals(p.Name, ruleRow.Name, StringComparison.OrdinalIgnoreCase)))
        {
            row.SetRuleState(0, 0, "未绑定", false);
        }

        RebuildRules();
        SetStatus(UiSeverity.Info, "规则已删除", $"{ruleRow.Name} 不再自动绑核。");
    }

    public void SetRuleEnabled(RuleRowViewModel ruleRow, bool enabled)
    {
        BindingRule? rule = _bindings.Profile.Rules
            .FirstOrDefault(r => string.Equals(r.Name, ruleRow.Name, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            return;

        rule.Enabled = enabled;
        _bindings.Save(_bindings.Profile.Rules.ToList());

        // 禁用后让监听循环丢掉「已处理」缓存，否则被禁用的规则不会立刻停止生效。
        _watcher.ResetCache();
        RebuildRules();
        SetStatus(UiSeverity.Info, enabled ? "规则已启用" : "规则已禁用", ruleRow.Name);
    }

    // ==================== 内部 ====================

    private int ApplyToRunning(string name, int targetIndex, int priorityIndex)
    {
        int applied = 0;

        foreach (ProcessRowViewModel row in Processes.Where(p =>
                     string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            bool ok = targetIndex switch
            {
                1 => _binder.BindToCcd(row.Pid, 0).Success,
                2 => _binder.BindToCcd(row.Pid, 1).Success,
                >= 3 => BindCustom(row.Pid, targetIndex),
                _ => true
            };

            if (priorityIndex > 0 && priorityIndex <= ProcessPriority.Options.Length)
            {
                PriorityApplyResult result = ProcessPriority.Apply(row.Pid, ProcessPriority.Options[priorityIndex - 1]);
                ok &= result.Failed == 0;
            }

            if (ok)
                applied++;
        }

        return applied;
    }

    private bool BindCustom(int pid, int targetIndex)
    {
        int maskIndex = targetIndex - 3;
        if (maskIndex < 0 || maskIndex >= _maskList.Count)
            return false;

        return _binder.BindToMask(pid, _maskList[maskIndex].Mask).Success;
    }

    private (BindingTarget Target, string MaskId, ulong Mask, string MaskName) ResolveTarget(int targetIndex)
    {
        if (targetIndex == 1)
            return (BindingTarget.CCD0, string.Empty, 0, string.Empty);
        if (targetIndex == 2)
            return (BindingTarget.CCD1, string.Empty, 0, string.Empty);

        int maskIndex = targetIndex - 3;
        if (maskIndex >= 0 && maskIndex < _maskList.Count)
        {
            CustomMask mask = _maskList[maskIndex];
            return (BindingTarget.Custom, mask.Id, mask.Mask, mask.Name);
        }

        return (BindingTarget.None, string.Empty, 0, string.Empty);
    }

    private int TargetIndexOf(BindingRule rule) => rule.Target switch
    {
        BindingTarget.CCD0 => 1,
        BindingTarget.CCD1 => 2,
        BindingTarget.Custom => 3 + _maskList.FindIndex(m => m.Id == rule.MaskId),
        _ => 0
    };

    private static int PriorityIndexOf(BindingRule rule)
    {
        if (string.IsNullOrEmpty(rule.Priority))
            return 0;

        int index = Array.IndexOf(ProcessPriority.Options, rule.Priority);
        return index < 0 ? 0 : index + 1;
    }

    private void ApplyRuleStateToRow(ProcessRowViewModel row)
    {
        BindingRule? rule = _bindings.LookupByName(row.Name);
        if (rule is null || !rule.Enabled || rule.Target == BindingTarget.None)
        {
            row.SetRuleState(0, 0, "未绑定", false);
            return;
        }

        row.SetRuleState(TargetIndexOf(rule), PriorityIndexOf(rule), rule.Label, true);
    }

    private void RebuildMaskOptions()
    {
        _maskList.Clear();
        _maskList.AddRange(_masks.Masks);

        while (TargetOptions.Count > 3)
            TargetOptions.RemoveAt(3);

        foreach (CustomMask mask in _maskList)
            TargetOptions.Add(string.IsNullOrWhiteSpace(mask.Name) ? MaskFormatter.ToRangeText(mask.Mask) : mask.Name);
    }

    private void RebuildRules()
    {
        Rules.Clear();

        foreach (BindingRule rule in _bindings.Profile.Rules)
        {
            int running = Processes.Count(p => string.Equals(p.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
            Rules.Add(new RuleRowViewModel(this,
                rule.Name,
                rule.Label,
                string.IsNullOrEmpty(rule.Priority) ? "优先级不变" : rule.Priority,
                rule.Enabled,
                running));
        }

        RulesCountText = Rules.Count.ToString();
        HasRules = Rules.Count > 0;
    }

    /// <summary>
    /// 规则行的「运行中实例数」。
    ///
    /// 这里是本页唯一一处每帧都会跑的全量统计，所以不能写成
    /// 「每条规则遍历一遍进程表」（规则 50 条 × 进程 400 个 = 每秒 2 万次比较）：
    /// 改成一次遍历建 名字→数量 的字典，再按规则查表。
    /// </summary>
    private void UpdateRuleMatchCounts()
    {
        if (Rules.Count == 0)
            return;

        _runningByName.Clear();
        foreach (ProcessRowViewModel process in Processes)
        {
            _runningByName.TryGetValue(process.Name, out int count);
            _runningByName[process.Name] = count + 1;
        }

        foreach (RuleRowViewModel ruleRow in Rules)
        {
            _runningByName.TryGetValue(ruleRow.Name, out int count);
            if (ruleRow.MatchingProcesses != count)
                ruleRow.MatchingProcesses = count;
        }
    }

    private void SyncWatcherState()
    {
        IsWatcherRunning = _watcher.IsRunning;
        WatcherText = _watcher.IsRunning
            ? (_watcher.UsingEvents ? "监听中 · 事件驱动" : "监听中 · 轮询降级")
            : "后台监听未启动";
        WatcherButtonText = _watcher.IsRunning ? "停止监听" : "启动监听";
    }

    private void SetStatus(UiSeverity level, string title, string message)
    {
        StatusLevel = level;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusVisible = true;
    }
}
