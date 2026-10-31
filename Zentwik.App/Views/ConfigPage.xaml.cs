using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;
using Zentwik.Core.Services;

namespace Zentwik.App.Views;

/// <summary>
/// 配置页。方案与规则可能在别的页被改动，所以每次进入都重算一遍概览 ——
/// 只读文本的重算是几微秒的事，换成事件订阅反而要处理取消订阅的时序。
/// </summary>
public sealed partial class ConfigPage : Page
{
    public ConfigPage(ConfigViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public ConfigViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    /// <summary>
    /// 行模板里的按钮拿不到页面级的 ViewModel（DataTemplate 的作用域是行数据），
    /// 所以直接用行数据本身作为参数，而不是给模板塞 ElementName 绑定。
    /// </summary>
    private void OnApplyProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileSnapshot profile })
            ViewModel.ApplyProfileCommand.Execute(profile.Id);
    }
}
