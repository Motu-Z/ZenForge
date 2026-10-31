using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;

namespace Zentwik.App.Views;

/// <summary>
/// 高级页。「清除全部数据」是不可撤销的，二次确认由 View 负责 ——
/// ViewModel 只暴露执行入口，避免「确认逻辑」和「业务逻辑」缠在一起。
/// </summary>
public sealed partial class AdvancedPage : Page
{
    public AdvancedPage(AdvancedViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AdvancedViewModel ViewModel { get; }

    private async void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        ContentDialogResult result = await ConfirmAsync();
        if (result != ContentDialogResult.Primary)
            return;

        ViewModel.ClearAllData();
    }

    private async System.Threading.Tasks.Task<ContentDialogResult> ConfirmAsync()
    {
        ContentDialog dialog = new()
        {
            // 非 XamlRoot 赋值不可选：非打包 WinUI 3 没有 CoreWindow，
            // 缺 XamlRoot 的对话框在 ShowAsync 时会直接抛 COMException。
            XamlRoot = XamlRoot,
            Title = "清除全部数据",
            Content = "调校、方案、调度规则、自定义掩码与界面设置都会被删除，且无法撤销。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync();
    }
}
