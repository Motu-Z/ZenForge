using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Zentwik.Core.Services;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 日志列表的一行。等级用三个布尔位暴露给模板，避免把 Brush 类型带进 ViewModel。
///
/// 继承 ObservableObject 只是为了满足 x:Bind 的 OneWay 契约（编译器要求绑定链上
/// 至少有一环支持变更通知）。本类是不可变的：日志一旦写入就不再变化，
/// 因此不抛任何通知 —— 列表行为靠 ItemsRepeater 换行时的 DataContext 变更驱动。
/// </summary>
public sealed class LogRowViewModel : ObservableObject
{
    public LogRowViewModel(LogEntry entry)
    {
        TimeText = UiConverters.ToTimeText(entry.Time);
        LevelText = UiConverters.ToLevelText(entry.Level);
        Message = entry.Message;
        Level = entry.Level;
    }

    public string TimeText { get; }

    public string LevelText { get; }

    public string Message { get; }

    public LogLevel Level { get; }

    public bool IsInfo => Level is LogLevel.Debug or LogLevel.Info;

    public bool IsWarn => Level == LogLevel.Warn;

    public bool IsError => Level is LogLevel.Error or LogLevel.Critical;

    public bool Matches(string keyword, bool info, bool warn, bool error)
    {
        if (IsInfo && !info)
            return false;
        if (IsWarn && !warn)
            return false;
        if (IsError && !error)
            return false;

        return keyword.Length == 0 ||
               Message.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
