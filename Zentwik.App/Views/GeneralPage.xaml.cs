using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;

namespace Zentwik.App.Views;

/// <summary>
/// 通用页。设置项在改动的当下就落盘（见 <see cref="SettingsViewModel"/>），
/// 所以这个页面没有任何生命周期回调 —— 它不需要在离开时保存什么。
/// </summary>
public sealed partial class GeneralPage : Page
{
    public GeneralPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
