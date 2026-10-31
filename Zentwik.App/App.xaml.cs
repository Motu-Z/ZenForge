using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Zentwik.App.Services;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.App;

/// <summary>
/// 界面对象。业务逻辑一律在 Core，这里只做三件事：
/// 建宿主、挂调度器、开窗。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // XAML 线程上的异常如果没人接，WinUI 默认是静默终止进程，
        // 用户看到的就是「双击没反应」。这里至少让它落进日志。
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 真正的 UI 调度器此刻才有：Application.Start 已经把 DispatcherQueue
        // 建在 STA 线程上，SynchronizationContext 也跟着可用了。
        SynchronizationContext? context = SynchronizationContext.Current;
        if (context is null)
        {
            context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
        }

        AppHost.Dispatcher.Attach(new SynchronizationContextDispatcher(context));

        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // WinUI 的 XAML 失败会以 stowed exception（0xc000027b）终止进程，
        // 事件查看器里只有一句「Microsoft.UI.Xaml.dll 出错」，没有堆栈。
        // 这条通道是唯一能在崩溃前把真实异常写下来的地方。
        StartupCrashReporter.Report(e.Exception, interactive: false);

        if (AppHost.Services.GetService(typeof(ILogService)) is ILogService log)
            log.Error($"UI 线程未处理异常：{e.Exception}");

        // 不置 Handled：带着未知状态继续跑，比崩掉更容易把用户配置写坏。
        // 让进程退出，日志已经落盘，下次启动能看到原因。
    }
}
