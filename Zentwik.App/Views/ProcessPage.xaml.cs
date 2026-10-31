using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;

namespace Zentwik.App.Views;

/// <summary>
/// 进程页。生命周期必须严格成对：Loaded 取采样引用计数，Unloaded 释放。
///
/// 少一次释放 = 采样线程永远不睡（收进托盘后仍每秒全量扫进程）；
/// 少一次获取 = 列表永远不刷新。两者都是「看起来只是慢/不更新」的软故障，很难查。
/// </summary>
public sealed partial class ProcessPage : Page
{
    public ProcessPage(ProcessViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ProcessViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Attach();

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Detach();
}
