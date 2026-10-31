using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Zentwik.App.ViewModels;

namespace Zentwik.App.Views;

/// <summary>
/// 调校页。View 只做三件事：把 ViewModel 交给 XAML、在生命周期点上通知 ViewModel、
/// 以及驱动「脏数据浮动保存条」的出入场动画。
///
/// 注意这里没有参数无关的构造：页面实例由外壳显式 new 出来并传入 ViewModel，
/// 不走 Frame.Navigate(typeof(T))。那条路要求无参构造，最终只能在页面里
/// new 一遍服务或从静态容器里捞 —— 两者都会把「构造函数注入」这条线破掉。
/// </summary>
public sealed partial class TuningPage : Page
{
    private readonly DispatcherQueueTimer _powerTimer;

    private bool _initialized;
    private bool _powerReady;
    private bool _powerApplying;
    private bool _dirtyAnimating;
    private bool _suppressUiDirty;

    public TuningPage(TuningViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;

        // 拖动滑块时每一帧都会触发 ValueChanged，而写一次电源策略要动多个注册表索引、
        // 再重新激活一次方案 —— 逐帧提交会让整机在拖动全程持续掉频。
        // 700ms 静默期 = 用户停手之后才提交一次。
        _powerTimer = DispatcherQueue.CreateTimer();
        _powerTimer.Interval = TimeSpan.FromMilliseconds(700);
        _powerTimer.IsRepeating = false;
        _powerTimer.Tick += OnPowerTimerTick;
    }

    public TuningViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 只初始化一次：切页回来不该重新读一遍 SMU（每次读都会打断刚下发的值）。
        if (_initialized)
            return;

        _initialized = true;
        // 初始化回填（SMU 快照 → FMaxText / 下拉 SelectedIndex）也会触发 TextChanged，
        // 那不是用户改动：全程压住保存条，回填结束才放开。
        _suppressUiDirty = true;
        try
        {
            await ViewModel.InitializeAsync();
        }
        finally
        {
            // 回填期间控件的 SelectedIndex / Value 会从默认值跳到真实值，
            // 那一串变更不是用户操作，不能当成「改动即提交」的指令。
            _powerReady = true;
            _suppressUiDirty = false;
        }
    }

    // ==================== 用户改动检测 ====================

    /// <summary>任何用户输入路径（TextBox / 下拉 / 滑块）最终都汇到这里。</summary>
    private void MarkDirty()
    {
        if (!_powerReady || _suppressUiDirty || ViewModel.IsBusy)
            return;

        ViewModel.MarkDirty();
        ShowDirtyBar();
    }

    // ==================== 浮动保存条 ====================

    private void ShowDirtyBar()
    {
        if (DirtyBar.Visibility == Visibility.Visible || _dirtyAnimating)
            return;

        _dirtyAnimating = true;
        DirtyBar.Opacity = 0;
        DirtyBarTransform.TranslateY = 20;
        DirtyBar.Visibility = Visibility.Visible;

        var slide = new DoubleAnimation
        {
            From = 20,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, DirtyBarTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180))
        };
        Storyboard.SetTarget(fade, DirtyBar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => _dirtyAnimating = false;
        storyboard.Begin();
    }

    private void HideDirtyBar()
    {
        _dirtyAnimating = false;
        DirtyBar.Visibility = Visibility.Collapsed;
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        ViewModel.DiscardDraftCommand.Execute(null);
        HideDirtyBar();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCommand.Execute(null);
        HideDirtyBar();
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveDraftAsCommand.Execute(null);
        HideDirtyBar();
    }

    // ==================== 输入事件 ====================

    /// <summary>
    /// TextChanged 既来自用户输入，也来自回填（初始化、方案切换、容器延迟 realize）。
    /// 只有当该 TextBox 持有焦点时才可能是用户在打字 —— 回填永远发生在无焦点状态。
    /// </summary>
    private static bool IsUserTyping(TextBox box)
        => box is { FocusState: not Microsoft.UI.Xaml.FocusState.Unfocused };

    private void OnCoTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserTyping(sender as TextBox))
            MarkDirty();
    }

    private void OnFMaxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserTyping(sender as TextBox))
            MarkDirty();
    }

    private void OnParkingTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserTyping(sender as TextBox))
            MarkDirty();
    }

    private void OnPowerTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _ = ApplyPowerAsync();
    }

    /// <summary>下拉框换项：立即提交，不必等静默期。</summary>
    private void OnPowerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_powerReady || _powerApplying)
            return;

        MarkDirty();
        _ = ApplyPowerAsync();
    }

    /// <summary>拖动滑块：只重置防抖计时器，停手 700ms 后才提交。</summary>
    private void OnParkingValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_powerReady || _powerApplying)
            return;

        MarkDirty();
        _powerTimer.Stop();
        _powerTimer.Start();
    }

    private async Task ApplyPowerAsync()
    {
        // 双守卫缺一不可：
        // _powerReady 挡初始化回填，_powerApplying 挡「提交 → 回读 → 再次触发变更」的自激环。
        if (!_powerReady || _powerApplying)
            return;

        _powerTimer.Stop();
        _powerApplying = true;
        try
        {
            await ViewModel.ApplyPowerCommand.ExecuteAsync(null);
        }
        finally
        {
            _powerApplying = false;
        }
    }
}
