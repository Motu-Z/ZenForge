using System;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.DependencyInjection;
using Zentwik.Core.Threading;

namespace Zentwik.Compat
{
    /// <summary>
    /// 组合根。
    ///
    /// 放在 WPF 程序集里但不引用任何 WPF 类型：<see cref="Program"/> 是 -silent 路径的入口，
    /// 它一旦碰到 <c>App</c>（基类是 Application），WPF 的整套程序集就会被加载进进程，
    /// 「后台模式不加载界面框架」的前提当场失效。
    /// </summary>
    internal static class Composition
    {
        /// <summary>界面就绪前先注入的占位调度器，由 <c>App.OnStartup</c> 挂载真实实现。</summary>
        internal static DelegatingUiDispatcher Dispatcher { get; } = new();

        internal static IServiceProvider Build()
        {
            ServiceCollection services = new();
            services.AddZentwikCore(Dispatcher);

            return services.BuildServiceProvider();
        }
    }
}
