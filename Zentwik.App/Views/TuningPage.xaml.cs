using System;
using System.ComponentModel;
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
    private bool _initialized;

    /// <summary>
    /// 初始化回填是否已结束。本标志只管 View 侧的事：回填期间不接受用户输入，
    /// 也不显示脏数据保存条。ViewModel 侧另有一份同义的闸门，管的是「允许提交电源策略」。
    /// </summary>
    private bool _powerReady;

    private bool _powerApplying;
    private bool _dirtyAnimating;
    private bool _suppressUiDirty;

    public TuningPage(TuningViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public TuningViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 后端清脏（应用 / 保存 / 放弃 / 同步）时保存条必须跟着收：
        // 方案名旁边的 * 角标消失了、底部保存条还杵在屏幕上，两处状态自相矛盾。
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面实例被缓存复用，重新 Loaded 时会再订阅；离开页面必须解绑，
        // 否则 ViewModel（单例）会一直持有这一页。
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TuningViewModel.IsDraftDirty))
            return;

        // 保存条的显隐完全由 IsDraftDirty 驱动，两个方向都走这一条路：
        //   置脏（用户输入 / 步进按钮）→ 弹条；
        //   清脏（应用 / 保存 / 放弃 / 同步）→ 收条。
        // 早先两边各管一半，于是出现「角标消失了、保存条还杵着」
        // 与「步进按钮改完值、保存条不弹」两种半吊子状态。
        if (ViewModel.IsDraftDirty)
            ShowDirtyBar();
        else
            HideDirtyBar();
    }

    /// <summary>任何用户输入路径（TextBox / 下拉 / 滑块）最终都汇到这里。</summary>
    private void MarkDirty()
    {
        if (!_powerReady || _suppressUiDirty || ViewModel.IsBusy)
            return;

        ViewModel.MarkDirty();
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
    private static bool IsUserTyping(TextBox? box)
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

    /// <summary>下拉框换项：立即提交，不必等静默期。</summary>
    private void OnPowerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_powerReady || _powerApplying)
            return;

        MarkDirty();
        _ = ApplyPowerAsync();
    }

    /// <summary>
    /// 拖动滑块：只把「用户改了值」报给 ViewModel，提交时机（停手 700ms）由它内部防抖决定。
    /// 防抖计时器不在 View 层 —— 计时与提交必须同层，否则两套计时器会互相触发对方的提交路径。
    /// </summary>
    private void OnParkingValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_powerReady || _powerApplying)
            return;

        MarkDirty();
        ViewModel.SchedulePowerApply();
    }

    private async Task ApplyPowerAsync()
    {
        // 双守卫缺一不可：
        // _powerReady 挡初始化回填，_powerApplying 挡「提交 → 回读 → 再次触发变更」的自激环。
        if (!_powerReady || _powerApplying)
            return;

        // 挂起中的防抖提交由 ViewModel 在本次提交开始时作废（代次递增），
        // View 层不需要也不应该再持有一份计时器。
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
