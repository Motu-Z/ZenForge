using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 纯后台宿主（-silent）。
    ///
    /// 与 GUI 路径的分界线：这里**不能出现任何 WPF 类型**，包括 App。
    /// 原因见 Program.cs 的注释 —— WPF 一旦实例化，常驻内存直接翻倍。
    ///
    /// 职责：
    ///   1. 单实例（静默实例不打扰已有实例，直接退）
    ///   2. 抢锁 + 跑 <see cref="SilentModeRunner"/>（CO/FMax + ProcessWatcher）
    ///   3. 判定是否常驻
    ///   4. 常驻时起原生托盘 + Win32 消息循环，等唤醒/退出
    ///   5. 收到唤醒 → 销毁原生托盘 → 交给 <see cref="GuiHost.Upgrade"/> 加载 WPF
    /// </summary>
    internal static class HeadlessHost
    {
        public static int Run(string[] args)
        {
            ApplyVerboseSwitch(args);

            // 已有实例：静默启动不该打断用户，也不该把界面弹出来
            if (SingleInstance.TrySignalExisting(signalMain: false))
            {
                LogService.Info("已有 ZenForge 实例在运行，静默实例退出");
                return 0;
            }

            if (!SingleInstance.TryAcquire())
            {
                LogService.Info("静默模式：已有 ZenForge 实例在运行，本实例退出");
                return 0;
            }

            // 静默任务可能要等 PawnIO 就绪（最长 60 秒）。这期间用户双击 exe 会走到
            // TrySignalExisting → 发唤醒事件，而此刻还没有托盘窗口可以投递消息。
            // 用一个信号把请求接住，跑完立刻升级，而不是让用户以为双击没反应。
            using ManualResetEventSlim upgradeSignal = new(false);
            SingleInstance.WakeRequested = upgradeSignal.Set;

            SilentRunResult result;
            try
            {
                result = SilentModeRunner.Run();
            }
            catch (Exception ex)
            {
                LogService.Error($"静默模式未处理异常：{ex}");
                result = new SilentRunResult();
            }

            if (upgradeSignal.IsSet)
            {
                LogService.Info("升级 1/6：静默任务期间收到界面请求，切换 WPF 宿主");
                SingleInstance.WakeRequested = null;
                return UpgradeSafely();
            }

            // 常驻的唯一理由是「持续绑核」。
            // 三个条件缺一不可：开关开着、有会生效的规则、且监听确实归本进程所有
            // （HasOwnership 用来区分「全局互斥体被别人占用」和「WMI 降级轮询但仍属自己」）。
            bool resident = UiSettingsStore.Settings.WatchProcessBinding
                         && result.HasActiveRules
                         && ProcessWatcher.HasOwnership;

            if (!resident)
            {
                LogService.Info("静默模式：没有需要常驻的绑核任务，进程退出");
                Cleanup();
                return 0;
            }

            // 自检：确认纯后台前提没被破坏（JIT 内联、静态构造等都可能把 WPF 拖进来）
            LogLoadedAssemblies();

            return RunResidentLoop();
        }

        /// <summary>
        /// 解析 -debug / --debug / ZENFORGE_DEBUG。
        ///
        /// App.OnStartup 里有一份同样的逻辑，但 -silent 根本不经过 App ——
        /// 那条路径上不补这一段，后台的详细日志就永远是关的。
        /// </summary>
        private static void ApplyVerboseSwitch(string[] args)
        {
            try
            {
                if (LogService.Verbose)
                    return;

                bool byArg = args.Any(a =>
                    string.Equals(a, "-debug", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));

                bool byEnv = string.Equals(
                    Environment.GetEnvironmentVariable("ZENFORGE_DEBUG"), "1", StringComparison.Ordinal);

                LogService.Verbose = byArg || byEnv;
            }
            catch
            {
                // 开关解析失败不应影响启动
            }
        }

        /// <summary>
        /// 常驻循环。托盘可关，但窗口必须建 —— 它是接收唤醒消息的唯一通道。
        /// 关掉托盘只是不 AddIcon，进程仍在后台等用户双击 exe。
        /// </summary>
        private static int RunResidentLoop()
        {
            NativeTrayIcon? tray = null;

            try
            {
                try
                {
                    tray = new NativeTrayIcon(showIcon: UiSettingsStore.Settings.ShowTrayIcon);

                    // 唤醒信号 → 投递到隐藏窗口 → 消息循环里 PostQuitMessage → 升级
                    SingleInstance.WakeRequested = tray.PostWake;

                    LogService.Info(UiSettingsStore.Settings.ShowTrayIcon
                        ? "静默模式：已进入后台常驻（原生托盘，未加载 WPF）"
                        : "静默模式：已进入后台常驻（无托盘，双击 ZenForge.exe 打开界面）");

                    tray.RunMessageLoop();
                }
                catch (Exception ex)
                {
                    // 托盘窗口建不出来也必须继续常驻 —— 绑核还在跑，不能因为
                    // 少了入口就把监听一起丢掉。退化成纯事件等待，双击 exe 仍能升级。
                    LogService.Warn($"托盘创建失败，降级为无窗口后台等待：{ex.Message}");

                    // 先拆掉半成品再升级：finally 要等 app.Run() 退出才执行，
                    // 那时 WPF 早已建好自己的托盘，任务栏会出现两个图标。
                    tray?.Dispose();
                    tray = null;

                    using ManualResetEventSlim signal = new(false);
                    SingleInstance.WakeRequested = signal.Set;
                    signal.Wait();

                    SingleInstance.WakeRequested = null;
                    return UpgradeSafely();
                }

                if (tray.UpgradeRequested)
                {
                    LogService.Info("升级 1/6：销毁原生托盘，准备加载 WPF 宿主");
                    SingleInstance.WakeRequested = null;

                    // 必须先拆掉原生托盘再加载 WPF：
                    // 否则 WPF 的 TrayIconService 会再建一个，任务栏出现两个图标。
                    tray.Dispose();
                    tray = null;

                    return UpgradeSafely();
                }

                LogService.Info("静默模式：收到退出请求，进程结束");
            }
            catch (Exception ex)
            {
                LogService.Error($"后台常驻失败：{ex}");
            }
            finally
            {
                tray?.Dispose();
            }

            Cleanup();
            return 0;
        }

        /// <summary>
        /// 加载 WPF 宿主，失败则回退为「有界面框架、无主窗口」的后台常驻。
        ///
        /// 0.8.0 的教训：这里原本是裸调 <see cref="GuiHost.Upgrade"/>，一旦升级链
        /// 上任何一步抛异常（当时是 App.OnStartup 里给 StartupUri 赋 null），
        /// 异常穿回 RunResidentLoop 的 catch，随后 Cleanup() 停掉监听、释放单实例锁，
        /// 进程直接消失 —— 用户看到的就是「点打开界面就闪退，绑核一起没了」。
        ///
        /// 现在的原则：界面可以起不来，职责不能丢。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int UpgradeSafely()
        {
            try
            {
                return GuiHost.Upgrade();
            }
            catch (Exception ex)
            {
                LogService.Error($"界面加载失败，回退为后台常驻（绑核未中断）：{ex}");
                return GuiFallback.Run();
            }
        }

        private static void Cleanup()
        {
            try { ProcessWatcher.Stop(); }
            catch { /* 忽略 */ }

            try { LogService.Flush(); }
            catch { /* 忽略 */ }

            SingleInstance.Release();
        }

        /// <summary>
        /// 把已加载程序集清单写进日志，作为「纯后台」的长期可查证据。
        /// 只要出现 PresentationFramework / PresentationCore / WindowsBase /
        /// System.Windows.Forms 之一，就说明轻量前提已经被破坏。
        /// </summary>
        private static void LogLoadedAssemblies()
        {
            try
            {
                string[] names = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetName().Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                LogService.Info($"后台宿主已加载 {names.Length} 个程序集：{string.Join(", ", names)}");

                bool hasUiFramework = names.Any(n =>
                    n.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("PresentationCore", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase));

                if (hasUiFramework)
                {
                    LogService.Warn(
                        "后台宿主加载了 UI 框架，纯后台前提被破坏。" +
                        "请检查 Program.Main 是否直接引用了 WPF 类型，或分派方法是否被内联。");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"程序集自检失败：{ex.Message}");
            }
        }
    }
}
