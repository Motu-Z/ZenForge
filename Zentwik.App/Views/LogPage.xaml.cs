using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zentwik.App.ViewModels;

namespace Zentwik.App.Views;

/// <summary>
/// 日志页。绑定的是 Core 里那个 Singleton 的内存日志，不是副本 ——
/// 副本会带来两套计数与两套淘汰节奏，最终表现为「页面 200 条、文件 3000 行」。
/// </summary>
public sealed partial class LogPage : Page
{
    public LogPage(LogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LogViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Attach();

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Detach();

    /// <summary>
    /// 打开日志目录。走 ShellExecute 而不是 Launcher：这是非打包应用，
    /// 没有 Package Identity，Launcher.LaunchFolderAsync 在无包标识时行为不确定。
    /// </summary>
    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // 目录不存在或资源管理器拒绝时静默失败：它只是个便利入口，
            // 不该为了打不开文件夹弹一个错误打断用户。
        }
    }
}
