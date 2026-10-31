using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;
using Zentwik.Core.Services;

namespace Zentwik.App;

/// <summary>
/// x:Bind 用的静态翻译函数集合。
///
/// 走「编译期函数」而不是 IValueConverter：{x:Bind} 直接编译成属性访问 + 方法调用，
/// 没有反射、没有装箱，也不用把转换器注册进资源字典。
/// </summary>
public static class UiConverters
{
    /// <summary>界面提示等级 → InfoBar 严重级别。</summary>
    public static InfoBarSeverity ToInfoBarSeverity(UiSeverity severity) => severity switch
    {
        UiSeverity.Success => InfoBarSeverity.Success,
        UiSeverity.Warning => InfoBarSeverity.Warning,
        UiSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    /// <summary>日志时间戳：只到秒，毫秒对排查无用且会把行撑宽。</summary>
    public static string ToTimeText(DateTime time) => time.ToString("HH:mm:ss");

    /// <summary>CO 值展示：正数带 + 号，0 就是 0，读不到用破折号。</summary>
    public static string ToSignedText(int value) => value switch
    {
        int.MinValue => "—",
        > 0 => $"+{value}",
        _ => value.ToString()
    };

    /// <summary>日志等级 → 短标签（与 Core 的 LogEntry.LevelText 同口径，供筛选徽章复用）。</summary>
    public static string ToLevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "INFO"
    };

    /// <summary>
    /// 可用状态 → 透明度。
    ///
    /// ToggleSwitch 的 IsEnabled 已经会让它变灰，但缩进的子项整体被禁用时，
    /// 只有控件自己变灰、标签文字仍是全亮，看起来像「标签还能点」。
    /// 让整组一起降到 0.4，视觉上才是「这一块现在不可用」。
    /// </summary>
    public static double ToEnabledOpacity(bool enabled) => enabled ? 1.0 : 0.4;

    /// <summary>
    /// 「没有内容」时才显示的空态。
    ///
    /// x:Bind 能把 bool 直接当 Visibility 用（true = 可见），但空态要的正好相反。
    /// 与其再引一个 IValueConverter 进资源字典，不如一个编译期函数。
    /// </summary>
    public static Visibility ToVisibleWhenFalse(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>「有内容时才显示」的外壳：表头、分隔线在空列表下应当整块收起。</summary>
    public static Visibility ToCollapsedWhenTrue(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>true 时显示（脏数据星号、徽章等正向标记）。</summary>
    public static Visibility ToVisibleWhenTrue(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;
}
