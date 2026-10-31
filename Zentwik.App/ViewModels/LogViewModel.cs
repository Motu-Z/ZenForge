using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 日志页。
///
/// 【为什么绑定同一个 ILogService 单例，而不是自己再攒一份】
/// 内存日志队列是 Singleton，上限 200 条、超出淘汰，由 Core 统一维护。
/// 界面若另建一份副本，就会出现两份计数、两套淘汰节奏，
/// 最终表现成「日志页显示 200 条，导出的文件里有 3000 行」。
/// 这里只维护**视图副本**：同一批数据按当前筛选条件过滤后的结果。
///
/// 【上限 200 条的界面表现】
/// 页脚常驻「当前 N / 上限 200」；满了之后每来一条就淘汰最旧一条，
/// 用户能看到「滚到底就是最早的记录」，而不是以为日志丢了。
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    /// <summary>与 Core 的 LogOptions 默认上限保持一致，仅用于文案。</summary>
    private const int Uplimit = 200;

    private readonly ILogService _log;

    public LogViewModel(ILogService log, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        // 契约校验：日志集合只在 UI 线程被改，dispatcher 必须是已挂载的那一个。
        ArgumentNullException.ThrowIfNull(dispatcher);

        Verbose = log.Verbose;
        Rebuild();
    }

    public ObservableCollection<LogRowViewModel> Rows { get; } = new();

    // ==================== 筛选 ====================

    [ObservableProperty]
    public partial bool ShowInfo { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWarn { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowError { get; set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Verbose { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    public partial bool IsPaused { get; set; }

    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    // ==================== 计数 ====================

    [ObservableProperty]
    public partial string InfoCountText { get; set; } = "0";

    [ObservableProperty]
    public partial string WarnCountText { get; set; } = "0";

    [ObservableProperty]
    public partial string ErrorCountText { get; set; } = "0";

    [ObservableProperty]
    public partial string FooterText { get; set; } = "0 / 200";

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "暂无日志";

    /// <summary>
    /// 空态是否显示。必须由列表真实条数驱动 —— 只靠文案切换会让「暂无日志」
    /// 和实际存在的日志行叠在一起（曾经就是这样：空态那段 TextBlock 一直可见）。
    /// </summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public string LogDirectory => _log.LogDirectory;

    // ==================== 命令 ====================

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Rows.Clear();
        RefreshCounters();
        EmptyText = "日志已清空";
    }

    // ==================== 生命周期 ====================

    public void Attach()
    {
        _log.EntryAdded += OnEntryAdded;
        Rebuild();
    }

    public void Detach() => _log.EntryAdded -= OnEntryAdded;

    // ==================== 内部 ====================

    private void OnEntryAdded(LogEntry entry)
    {
        // 契约：EntryAdded 在 UI 线程触发（Core 用 IUiDispatcher 封送过），
        // 所以这里可以直接改集合。
        LogRowViewModel row = new(entry);
        if (row.Matches(SearchText, ShowInfo, ShowWarn, ShowError))
        {
            Rows.Add(row);
            while (Rows.Count > Uplimit)
                Rows.RemoveAt(0);
        }

        RefreshCounters();
    }

    private void Rebuild()
    {
        Rows.Clear();
        string keyword = SearchText.Trim();

        foreach (LogEntry entry in _log.Snapshot())
        {
            LogRowViewModel row = new(entry);
            if (row.Matches(keyword, ShowInfo, ShowWarn, ShowError))
                Rows.Add(row);
        }

        while (Rows.Count > Uplimit)
            Rows.RemoveAt(0);

        RefreshCounters();
    }

    private void RefreshCounters()
    {
        InfoCountText = _log.CountInfo.ToString();
        WarnCountText = _log.CountWarn.ToString();
        ErrorCountText = (_log.CountError + _log.CountCritical).ToString();
        FooterText = $"{Rows.Count} / {Uplimit}";
        EmptyText = IsPaused ? "已暂停记录" : "暂无日志";
        IsEmpty = Rows.Count == 0;
    }

    partial void OnShowInfoChanged(bool value) => Rebuild();

    partial void OnShowWarnChanged(bool value) => Rebuild();

    partial void OnShowErrorChanged(bool value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnVerboseChanged(bool value) => _log.Verbose = value;

    partial void OnIsPausedChanged(bool value)
    {
        // 暂停期间日志真的丢失（Core 的语义），不是「界面不动但仍在收集」。
        // 文案必须说清这一点，否则用户会以为暂停只是「先别刷屏」。
        _log.Paused = value;
        EmptyText = value ? "已暂停记录（新日志不会写入）" : "暂无日志";
        FooterText = value ? "已暂停" : $"{Rows.Count} / {Uplimit}";
    }
}
