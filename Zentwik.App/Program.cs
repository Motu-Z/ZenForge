using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Zentwik.App.Services;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.App;

/// <summary>
/// 进程入口。取代 XAML 自动生成的 Main（见 csproj 的 DISABLE_XAML_GENERATED_MAIN）。
///
/// 【为什么值得自己写这一层】
/// --silent 是开机自启用的路径：它只需要绑核，不需要窗口。但只要碰一下
/// <c>Application</c> 类型，WinUI 的 XAML 引擎、Composition、DWM 通道就全部进进程。
/// 所以判定必须发生在任何 WinUI 类型被 JIT 解析之前 —— 这也是 Main 里
/// 不出现 <c>new App()</c>、而是把创建动作塞进 lambda 的原因。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = IsSilent(args);

        try
        {
            // 容器先建：异常兜底与日志在界面存在之前就要可用。
            IServiceProvider services = AppHost.Build();
            ILogService log = services.GetRequiredService<ILogService>();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                log.Error($"进程未处理异常（即将终止）：{e.ExceptionObject}");

            if (silent)
                return RunHeadless(services, log);

            // CsWinRT 的 COM 封送基础设施必须在 Application.Start 之前初始化。
            // 少了这一句，第一个 WinRT 调用会以 InvalidCastException 的形式炸在
            // 与真实原因完全无关的地方。
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(static _ =>
            {
                DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

                // App 自己由 XAML 框架接管生命周期：这里只负责实例化，
                // 之后的消息循环由 Application.Start 维持。
                new App();
            });

            return 0;
        }
        catch (Exception ex)
        {
            // WinUI 的启动失败尤其安静：窗口没建起来就没有任何宿主 UI 能承载错误，
            // 事件查看器只留下 0xe0434352。先落盘，再原样抛出。
            StartupCrashReporter.Report(ex, interactive: !silent);
            throw;
        }
    }

    /// <summary>
    /// 后台模式。S4 骨架阶段只做三件事：确认容器可用、写一条日志、正常退出。
    /// 真正的绑核循环在 S5 接线，届时这里替换为 Core 的 headless 服务调度。
    /// </summary>
    private static int RunHeadless(IServiceProvider services, ILogService log)
    {
        log.Info("后台模式启动（--silent），未加载任何 UI 程序集。");
        log.Flush();
        return 0;
    }

    /// <summary>
    /// 判定 --silent。与 WPF 线保持同一套宽容规则：前置分隔符与大小写都不敏感，
    /// 计划任务里的参数经常被不同工具写成 <c>-silent</c> / <c>/silent</c>。
    /// </summary>
    private static bool IsSilent(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (string.Equals(arg.Trim().TrimStart('-', '/', '—'), "silent", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
