using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;

namespace Zentwik.App.Views;

/// <summary>
/// 行为页。开关拨动即落盘，页面本身不需要在离开时做任何收尾。
///
/// 唯一的时序要求：初始化回填必须在首次布局之后 ——
/// 在构造函数里把 IsOn 从 false 改成真值会触发一整轮 ToggleSwitch 的切换动画，
/// 用户看到的是「刚打开这一页，开关自己动了一下」。
/// </summary>
public sealed partial class BehaviorPage : Page
{
    private bool _initialized;

    public BehaviorPage(BehaviorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public BehaviorViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        ViewModel.Initialize();
    }
}
