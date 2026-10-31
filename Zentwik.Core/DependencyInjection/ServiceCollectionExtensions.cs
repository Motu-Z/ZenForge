using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.Core.DependencyInjection
{
    /// <summary>
    /// Core 的服务注册入口。
    ///
    /// Core 只引用 <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>，
    /// 不引用 Hosting：容器由宿主（WinUI 3 / WPF / 控制台自检）创建，
    /// Core 只负责「把服务按什么生命周期登记进去」。
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册 Core 的全部服务。
        /// </summary>
        /// <param name="services">宿主容器。</param>
        /// <param name="dispatcher">
        /// UI 线程调度器。WinUI 3 传 <c>DispatcherQueue.AsSynchronizationContext()</c> 包出来的实例；
        /// 无界面的后台进程（-silent）传 null，日志将不再跨线程封送。
        /// </param>
        /// <param name="configureLog">日志参数。为 null 时使用默认值（临时目录、200 条上限）。</param>
        public static IServiceCollection AddZentwikCore(
            this IServiceCollection services,
            IUiDispatcher? dispatcher = null,
            Action<LogOptions>? configureLog = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            // ---------- 基础设施 ----------
            // 先登记具体类型，再把接口映射到同一个实例。
            //
            // 【为什么必须两个都登记，不能只写 TryAddSingleton<IAppPaths, AppPaths>()】
            // 那种写法只登记接口，具体类型不在容器里。WPF 过渡桥（Svc.Paths）与
            // HeadlessHost 都是按具体类型取的，取不到就抛
            // InvalidOperationException: No service for type ... —— 而且是在
            // App.OnStartup 里抛，表现为「双击秒崩、没有窗口、事件查看器只有
            // 0xe0434352」。v2.0.0-can2 的启动崩溃就是这么来的。
            services.TryAddSingleton<AppPaths>();
            services.TryAddSingleton<IAppPaths>(provider => provider.GetRequiredService<AppPaths>());

            services.TryAddSingleton<LogService>(provider =>
            {
                LogOptions options = new();
                configureLog?.Invoke(options);

                // 未显式指定目录时落到 %AppData%\Zentwik\Logs
                if (string.IsNullOrWhiteSpace(options.Directory))
                    options.Directory = provider.GetRequiredService<IAppPaths>().Logs;

                return new LogService(options, dispatcher);
            });
            services.TryAddSingleton<ILogService>(provider => provider.GetRequiredService<LogService>());

            if (dispatcher is not null)
                services.TryAddSingleton(dispatcher);

            // ---------- 持久化 ----------
            // 先登记具体类型，再把接口映射到同一个实例：
            // 两个分开的 TryAddSingleton 会造出两个对象，接口与具体类型各持一份状态。
            services.TryAddSingleton<MaskStore>();
            services.TryAddSingleton<IMaskStore>(provider => provider.GetRequiredService<MaskStore>());

            services.TryAddSingleton<BindingStore>();
            services.TryAddSingleton<IBindingStore>(provider => provider.GetRequiredService<BindingStore>());

            services.TryAddSingleton<TuningStore>();
            services.TryAddSingleton<ITuningStore>(provider => provider.GetRequiredService<TuningStore>());

            services.TryAddSingleton<UiSettingsStore>();
            services.TryAddSingleton<IUiSettingsStore>(provider => provider.GetRequiredService<UiSettingsStore>());

            // ---------- CPU 访问 ----------
            // SmuReader 必须是单例：进程内只允许存在一个 Cpu 实例，
            // Opcode 的代码缓存是进程级静态资源，多一个实例就会互相打哑。
            services.TryAddSingleton<SmuReader>();
            services.TryAddSingleton<ISmuReader>(provider => provider.GetRequiredService<SmuReader>());

            services.TryAddSingleton<CpuSetBinder>();
            services.TryAddSingleton<ICpuSetBinder>(provider => provider.GetRequiredService<CpuSetBinder>());

            services.TryAddSingleton<LogThrottle>();
            services.TryAddSingleton<ProcessRefresher>();
            services.TryAddSingleton<PowerStatusWatcher>();
            services.TryAddSingleton<MemoryTrimmer>();
            services.TryAddSingleton<AutoStartService>();

            services.TryAddSingleton<ProcessWatcher>();
            services.TryAddSingleton<IProcessWatcher>(provider => provider.GetRequiredService<ProcessWatcher>());

            // ---------- 业务编排 ----------
            services.TryAddSingleton<TuningApplyService>();
            services.TryAddSingleton<ProfileService>();
            services.TryAddSingleton<ScenarioService>();
            services.TryAddSingleton<ScenarioMonitor>();
            services.TryAddSingleton<ScenarioSelfCheck>();
            services.TryAddSingleton<SmuDiagnostics>();

            return services;
        }
    }
}
