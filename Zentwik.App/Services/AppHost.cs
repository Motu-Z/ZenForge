using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.App.ViewModels;
using Zentwik.Core.DependencyInjection;
using Zentwik.Core.Threading;

namespace Zentwik.App.Services;

/// <summary>
/// 组合根。
///
/// 【为什么容器在 Main 里建、而不是在 App.OnLaunched 里】
/// App 的基类是 <c>Microsoft.UI.Xaml.Application</c>，只要它被实例化，
/// WinUI 的整套原生依赖就已经进进程了。而 --silent 那条路径要的恰恰是
/// 「一行 UI 代码都不加载」，所以容器必须先于 App 存在，App 只负责把
/// 真实的 UI 调度器挂上去。
///
/// 【为什么用可延后挂载的调度器】
/// IUiDispatcher 的实现需要 WinUI 的 SynchronizationContext，而它只有
/// Application.Start 之后才存在。DelegatingUiDispatcher 在未挂载时
/// 直接就地执行回调，正好覆盖后台模式；挂载后转发到 UI 线程。
/// </summary>
internal static class AppHost
{
    private static IServiceProvider? _services;

    internal static DelegatingUiDispatcher Dispatcher { get; } = new();

    /// <summary>容器。未调用 <see cref="Build"/> 前访问即为编程错误。</summary>
    internal static IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("AppHost.Build() has not been called.");

    /// <summary>建容器。重复调用返回同一实例，Main 与测试入口都可以安全调用。</summary>
    internal static IServiceProvider Build()
    {
        if (_services is not null)
            return _services;

        ServiceCollection services = new();

        // Core 只把「服务按什么生命周期登记」交出来，容器实现留在宿主。
        services.AddZentwikCore(Dispatcher);

        // ---------- 外壳层 ----------
        // 主题控制器与三个 ViewModel 都是单例：
        //   · ThemeController 持有窗口引用与标题栏通道，多实例会互相覆盖配色；
        //   · ViewModel 持有页面级状态（CO 草稿、筛选条件、采样订阅计数），
        //     页面实例已在外壳里缓存，再给 ViewModel 做瞬时生命周期只会让状态错位。
        services.TryAddSingleton<ThemeController>();
        services.TryAddSingleton<SettingsViewModel>();
        services.TryAddSingleton<TuningViewModel>();
        services.TryAddSingleton<ProcessViewModel>();
        services.TryAddSingleton<LogViewModel>();
        services.TryAddSingleton<ConfigViewModel>();
        services.TryAddSingleton<BehaviorViewModel>();
        services.TryAddSingleton<AdvancedViewModel>();

        _services = services.BuildServiceProvider();
        return _services;
    }
}
