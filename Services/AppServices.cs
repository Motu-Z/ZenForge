using System;
using Microsoft.Extensions.DependencyInjection;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Compat
{
    /// <summary>
    /// WPF 遗留层到 DI 容器的过渡桥。
    ///
    /// 【为什么存在】
    /// Core 已经全面改为构造函数注入，WPF 的 20 多个视图里还有约 900 处
    /// <c>LogService.Info(...)</c> 这类静态调用。把视图逐个改成构造函数注入，
    /// 等于给即将被 WinUI 3 页面整体替换的代码做精装修。
    ///
    /// 【删除条件】
    /// WinUI 3 页面接管全部视图后，本文件与 Zentwik.Compat 命名空间一并删除。
    /// 在此之前，新代码不得引用 <see cref="Svc"/> —— 需要服务就注入接口。
    /// </summary>
    internal static class Svc
    {
        private static IServiceProvider? _provider;

        internal static void Initialize(IServiceProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _provider = provider;
        }

        private static T Get<T>() where T : notnull
        {
            IServiceProvider provider = _provider
                ?? throw new InvalidOperationException("Svc 未初始化：组合根必须先于任何服务访问运行");

            return provider.GetRequiredService<T>();
        }

        internal static LogService Log => Get<LogService>();

        internal static SmuReader Smu => Get<SmuReader>();

        internal static AppPaths Paths => Get<AppPaths>();

        internal static MaskStore Masks => Get<MaskStore>();

        internal static BindingStore Bindings => Get<BindingStore>();

        internal static TuningStore Tuning => Get<TuningStore>();

        internal static UiSettingsStore Ui => Get<UiSettingsStore>();

        internal static CpuSetBinder CpuSets => Get<CpuSetBinder>();

        internal static ProcessWatcher Watcher => Get<ProcessWatcher>();

        internal static ProcessRefresher Refresher => Get<ProcessRefresher>();

        internal static PowerStatusWatcher Power => Get<PowerStatusWatcher>();

        internal static MemoryTrimmer Memory => Get<MemoryTrimmer>();

        internal static AutoStartService AutoStart => Get<AutoStartService>();

        internal static TuningApplyService TuningApply => Get<TuningApplyService>();

        internal static ProfileService Profiles => Get<ProfileService>();

        internal static ScenarioService Scenarios => Get<ScenarioService>();

        internal static ScenarioMonitor Monitor => Get<ScenarioMonitor>();

        internal static ScenarioSelfCheck SelfCheck => Get<ScenarioSelfCheck>();

        internal static SmuDiagnostics Diag => Get<SmuDiagnostics>();
    }
}
