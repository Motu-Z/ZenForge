using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ZenForge.Helpers;
using ZenForge.Services;
using ZenForge.Views;

namespace ZenForge
{
    /// <summary>
    /// 应用入口：单实例仲裁、提权、启动路径编排、全局异常兜底。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 真退出开关。置位后主窗口关闭不再被拦成「收进托盘」。
        /// 只有真正要结束进程时才置它：托盘菜单退出、无需常驻的静默启动。
        /// </summary>
        public static bool AllowExit;

        /// <summary>
        /// 本次 WPF 实例是从 -silent 后台宿主升级而来。
        ///
        /// 升级路径与正常启动的差别极大：单实例锁、ProcessWatcher、管理员权限
        /// 都已经由后台宿主持有。这里若再走一遍 TrySignalExisting / TryAcquire /
        /// EnsureAdministrator，轻则弹出多余的 UAC，重则 runas 重启把锁和监听全丢。
        /// </summary>
        public static bool IsHeadlessUpgrade;

        /// <summary>退出流程是否已经走过。Shutdown 只能调一次，重复调用会抛异常。</summary>
        private static bool _exiting;

        /// <summary>
        /// 「后台回退」状态下的退出信号。
        ///
        /// 场景：后台宿主升级界面失败，WPF 已加载但界面没建起来，进程退回到
        /// 「有 WPF 托盘、无主窗口」的常驻状态等在这里。此时 <see cref="ExitApplication"/>
        /// 不能只调 Application.Shutdown —— 那条路径要求 Application 已经 Run 起来，
        /// 而回退状态根本没进 Dispatcher 循环。所以由宿主把自己这个信号交出来，
        /// 退出时直接 Set 一下把它放走。
        /// </summary>
        public static System.Threading.ManualResetEventSlim? BackgroundExitSignal;

        /// <summary>
        /// 面向用户的版本号，取自 AssemblyInformationalVersion。
        ///
        /// 不用 <c>GetName().Version</c>：那是四段数字（0.9.0.0），
        /// 而后缀（-can1 / -rc1）只存在于 InformationalVersion。
        /// 测试版和正式版在界面上必须能一眼分开，否则用户报问题时分不清跑的是哪个。
        /// 曾经被 SDK 追加的 "+&lt;commit&gt;" 已在 csproj 里关掉。
        /// </summary>
        public static string VersionText { get; } = ReadVersionText();

        private static string ReadVersionText()
        {
            try
            {
                System.Reflection.Assembly asm = typeof(App).Assembly;

                string? info = asm
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    is object[] { Length: > 0 } attrs
                    && attrs[0] is System.Reflection.AssemblyInformationalVersionAttribute a
                        ? a.InformationalVersion
                        : null;

                if (!string.IsNullOrWhiteSpace(info))
                {
                    // 兜底：万一哪天又带上 +commit，只显示版本本身
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info[..plus] : info;
                }

                return asm.GetName().Version?.ToString(3) ?? "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        private static MainWindow? _mainWindow;

        internal static void SetMainWindow(MainWindow window) => _mainWindow = window;

        internal static void ReleaseMainWindow(MainWindow window)
        {
            if (ReferenceEquals(_mainWindow, window))
                _mainWindow = null;
        }

        /// <summary>
        /// 真正的主窗口。
        ///
        /// 为什么不直接用 Application.MainWindow：
        ///   · 托盘菜单宿主是第一个被 Show 出来的 Window，WPF 会把 MainWindow 指到它身上，
        ///     拿它当宿主（当对话框 Owner、当导航目标）弹窗会跑到屏幕 (-10000,-10000) 去；
        ///   · 轻量模式「仅留服务进程」会把主窗口整个销毁，此时 MainWindow 指向的是一个
        ///     已经 Closed 的残骸，谁用它谁炸。
        ///
        /// 所以主窗口实例由 App 自己持有，创建与销毁都走 <see cref="SetMainWindow"/> /
        /// <see cref="ReleaseMainWindow"/>。
        /// </summary>
        public static Window? RealMainWindow => _mainWindow;

        public static MainWindow? CurrentMainWindow => _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            // ==================== 后台宿主升级路径 ====================
            // 从 -silent 升级而来：锁、监听、权限都已就绪，只把 WPF 壳搭起来。
            // 必须放在最前面 —— 下面的渲染兜底、单实例探测、提权都不能再跑一遍。
            if (IsHeadlessUpgrade)
            {
                // 渲染模式必须在主窗口创建前定死，与正常启动同一理由
                try
                {
                    System.Windows.Media.RenderOptions.ProcessRenderMode =
                        System.Windows.Interop.RenderMode.SoftwareOnly;
                }
                catch { }

                // 绝不能碰 StartupUri：它的类型是 Uri，setter 内部 ThrowIfNull，
                // 赋 null 必抛 ArgumentNullException（0.8.0 的闪退就是这一行）。
                // 升级路径本来也不需要清它 —— InitializeComponent 已把它设为
                // MainWindow.xaml，而主窗口由下面 ShowOrCreateMainWindow 显式 new。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // 同步上下文必须最先挂：后面每一步都有可能产生日志，
                // 而 WMI 回调线程也会往日志集合里写，没有 UiContext 就是跨线程改
                // ObservableCollection —— 直接抛 InvalidOperationException。
                LogService.UiContext = SynchronizationContext.Current;

                // 全局异常兜底。正常分支有一份，升级分支以前漏了：
                // 这条链上任何一步抛异常都不会再表现为「静默闪退」，
                // 而是弹一个能看见的框 + 日志里留全栈。
                DispatcherUnhandledException += OnDispatcherUnhandledException;

                base.OnStartup(e);

                // 升级后本进程就是完整的 GUI 实例，退出语义与正常启动一致：
                // 托盘「退出」= 真退出，监听和锁一起收摊。
                // （要保住监听的只有「升级失败回退」那种状态，它由 GuiFallback 负责，
                //   根本不会走到这条 Exit 事件。）
                Exit += (_, _) => HardStop();

                LogService.Info("升级 3/6：加载设置 / 主题 / 存储");

                AttachWakeChannel();
                DisableFocusVisuals();
                LoadStores();

                LogService.Info("升级 4/6：启动进程采样与绑核回调");

                // 调度页的进程列表靠它采样。升级路径同样要立起循环 ——
                // 循环本身挂在唤醒信号上，没人看调度页就不采，不会增加后台开销。
                ProcessRefresher.Start();

                AttachProcessWatcherUi();

                LogService.Info("升级 5/6：创建 WPF 托盘");

                if (UiSettingsStore.Settings.ShowTrayIcon)
                    TrayIconService.EnsureCreated();

                LogService.Info("升级 6/6：创建主窗口");

                TrayIconService.ShowOrCreateMainWindow();

                LogService.Info("升级完成 · ZenForge 界面已从后台宿主加载（未重新抢锁、未重复写 SMU）");
                return;
            }

            // ==================== 渲染兜底 ====================
            // 必须放在 base.OnStartup(e) 之前：基类会按 StartupUri 创建主窗口，
            // 一旦窗口先建起来，渲染模式就已经定死了。
            //
            // 症状：双显卡笔记本上 WPF 偶尔会挑到不可用的硬件适配器，
            // 表现为进程活着、布局在算、日志照写，但客户区整片纯色（像 UI 缺了一块）。
            // 本工具以静态布局为主，软件渲染的开销可以忽略，直接固定 SoftwareOnly。
            // NvForge 早先遇到过同一个现象，用的是同一处写法。
            try
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode =
                    System.Windows.Interop.RenderMode.SoftwareOnly;
            }
            catch { }

            // ==================== 详细日志开关 ====================
            // 三种开启方式，任一满足即可（顺序即优先级）：
            //   1. 诊断版 exe 编译时已默认开启（VERBOSE_LOG）；
            //   2. 命令行 -debug / --debug；
            //   3. 环境变量 ZENFORGE_DEBUG=1。
            // 放在 base.OnStartup 之前，保证窗口构造期间的日志也能被记录。
            EnableVerboseLogIfRequested(e.Args);

            // ==================== 单实例：先探测 ====================
            // 必须在 base.OnStartup 之前：基类会按 StartupUri 把主窗口造出来并显示，
            // 之后再判断就会先闪一下窗口、白跑一遍页面布局，然后才退出。
            //
            // 放在提权之前同理：已有实例时直接唤醒它并退出，连 UAC 都不该弹。
            bool silent = IsSilentLaunch(e.Args);

            if (SingleInstance.TrySignalExisting(signalMain: !silent))
            {
                LogService.Info(silent
                    ? "已有 ZenForge 实例在运行，静默实例退出（不打扰已有实例）"
                    : "已有 ZenForge 实例在运行，本实例已请求其唤出主窗口并退出");

                // 同样不能赋 StartupUri = null。OnExplicitShutdown + 立即 Shutdown
                // 足以保证这一轮什么窗口都不建，不需要去清那个不可为 null 的属性。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 进程级收摊兜底：无论从哪条路径退出，监听和托盘都要被收拾干净。
            // 有了这条，OnExplicitShutdown 下就不会漏出「窗口没了但进程还在」的僵尸。
            Exit += (_, _) => HardStop();

            // 注销 / 关机只记一行，不在这里收摊：
            // 别的程序可以取消 SessionEnding，那时系统不关机、程序还得接着干活，
            // 提前把监听和托盘拆了就是自己把自己弄瘫。真要退出时 Exit 会照常触发。
            SessionEnding += (_, _) => LogService.Info("系统正在注销 / 关机");

            LogService.Info($"ZenForge 启动 · 详细日志：{(LogService.Verbose ? "已开启" : "关闭")}"
                + $" · 版本 {VersionText}");

            // ==================== .NET 10 桌面运行时检测 ====================
            // 本程序为「框架依赖」发布，不自带 .NET 运行时。
            // 用户直接双击 ZenForge.exe 运行时，apphost 会先拦截低版本运行时；
            // 此处再兜底：万一在低版本运行时上跑起来，给出友好提示后退出。
            if (!EnsureDotNetRuntime())
            {
                Shutdown();
                return;
            }

            // ==================== 后台进程快照采样 ====================
            // 这里只是把采样循环立起来，不等于立刻开始采集：循环会挂在唤醒信号上
            // 等待，直到「调度」页真的被用户看到（页面 Loaded + 窗口可见 + 未最小化）
            // 才开始按 IntervalSeconds 采样。没人看就完全不跑，省掉全部开销。
            Services.ProcessRefresher.Start();

            // ==================== 静默模式（-silent）拦截 ====================
            // 注意：-silent 已经在 Program.Main 里被拦截，走 HeadlessHost（不加载 WPF），
            // 正常情况下这里永远不会命中。保留这段仅作防御：万一将来有人绕过
            // Program 直接 new App，也不会先按 StartupUri 把窗口闪出来。
            //
            // 必须放在最前面：否则 StartupUri 会先把主窗口创建出来。
            // 这里只改 ShutdownMode —— StartupUri 是不可为 null 的 Uri 属性，
            // 清它是取死路；OnExplicitShutdown 已经足够保证后台任务做完就退。
            if (silent)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                LogService.UiContext = SynchronizationContext.Current;

                // 方案 A 下清单为 asInvoker，静默启动不再由清单强制提权。
                // 计划任务以 HighestAvailable 拉起时进程已是管理员，SMU 可直接使用；
                // 若是用户手动 ZenForge.exe -silent 且未提权，则只记日志、**绝不弹 UAC**
                // ——静默场景不允许打断用户，也不允许留下等待中的对话框。
                if (IsElevated())
                {
                    LogService.Info("静默模式：已具备管理员权限，SMU 可用");
                }
                else
                {
                    LogService.Warn(
                        "静默模式：当前未提权，SMU 写入将失败。" +
                        "若由计划任务启动，请检查任务的「以最高权限运行」是否生效。");
                }

                // 静默启动也走同一套单实例：计划任务拉起的实例和用户手动开的界面
                // 必须是同一个进程。以前两边各开各的，第二个抢不到 ProcessWatcher 的
                // 全局互斥体，监听静默失效 —— 这正是「开了开关却没在绑核」的来源。
                if (!SingleInstance.TryAcquire())
                {
                    LogService.Info("静默模式：已有 ZenForge 实例在运行，本实例退出");
                    Shutdown();
                    return;
                }

                SilentRunResult runResult = new();
                try
                {
                    runResult = SilentModeRunner.Run();
                }
                catch (Exception ex)
                {
                    LogService.Error($"静默模式未处理异常：{ex}");
                }

                // 关键：如果启动了进程绑核监听，进程必须常驻，否则 Shutdown 会立刻
                // 杀掉后台轮询线程，监听形同虚设（这是静默自启最容易踩的坑）。
                if (runResult.WatcherStarted && ProcessWatcher.IsRunning)
                {
                    LogService.Info("静默模式：进程绑核监听已常驻后台（无窗口，托盘可见）");

                    // 托盘是常驻实例唯一的门面：没有它，用户既叫不出界面也退不掉进程，
                    // 只能在任务管理器里杀 —— 这正是以前「任务管理器里找不到进程」的对照面。
                    //
                    // 这里不再用看门狗定时自杀：以前那个 10 秒一次的守卫只按 IsRunning
                    // 判断，用户从托盘暂停绑核时会被它误杀。进程现在看得见、退得掉，
                    // 僵尸风险由 Exit 事件兜底就够了。
                    //
                    // 托盘建不出来的话，继续常驻却无人可见是危险的；但此刻退出等于
                    // 放弃绑核。两害相权取其轻：记一条错误日志后照常常驻。
                    // 图标是内嵌资源，这条分支基本不会走到。
                    if (!TrayIconService.EnsureCreated())
                    {
                        LogService.Error("静默常驻实例没有托盘入口，用户只能在任务管理器里结束它");
                    }

                    return; // 不 Shutdown
                }

                LogService.Info("静默模式：无需常驻（未开启后台监听或监听启动失败），进程退出");
                ExitApplication();
                return;
            }

            // SMU 只读探测需要管理员权限。未提权时自动以 runas 重启自身，
            // 提权副本启动后本实例退出；用户拒绝 UAC 时以普通权限继续（读取会失败并提示）。
            if (!EnsureAdministrator(e.Args))
            {
                Shutdown();
                return;
            }

            // ==================== 单实例：正式占坑 ====================
            // 放在提权之后：父进程在提权前只探测不占坑，于是 runas 出来的
            // 子进程到这里才能顺利拿到互斥体。顺序反了会导致子进程把自己
            // 当成第二实例、唤醒父进程后退出，而父进程随后也 Shutdown —— 一个窗口都不剩。
            if (!SingleInstance.TryAcquire())
            {
                LogService.Info("已有 ZenForge 实例在运行，本实例退出（已请求其唤出主窗口）");
                Shutdown();
                return;
            }

            // 关窗不再等于退出进程：必须显式声明，默认的 OnMainWindowClose
            // 会在窗口一关就杀掉整个进程，后台服务随之消失。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // ==================== 唤醒通道：消息窗优先，命名事件兜底 ====================
            // 为什么必须有消息窗：后台可能由计划任务以「最高权限」拉起，而用户双击 exe
            // 启动的是中等权限。低权限 → 高权限方向，EventWaitHandle.OpenExisting
            // 会被 ACL 拒绝，于是「明明有一个在跑，双击却没反应，还多一个幽灵进程」。
            // 窗口消息不受这个限制，只要对方把消息加进 UIPI 白名单。
            AttachWakeChannel();

            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 日志服务在 UI 线程上下文中注册，后台线程写日志时自动封送回 UI 线程
            LogService.UiContext = SynchronizationContext.Current;

            // 全局禁用焦点虚线框（必须在任何控件被创建之前执行）
            DisableFocusVisuals();

            LoadStores();

            // 按设置启动后台调度监听（轮询 bindings.json）。
            // 总闸「开启后台服务」关着就不启动 —— 用户没要后台，不该悄悄占着全局互斥体。
            AttachProcessWatcherUi();

            if (UiSettingsStore.Settings.BackendService
                && UiSettingsStore.Settings.WatchProcessBinding)
            {
                ProcessWatcher.Start();

                // 启动失败不能被静默吞掉。典型场景是另一个实例占着全局互斥体：
                // 此时开关在界面上显示「开」，实际一次都没绑过。
                if (!ProcessWatcher.IsRunning)
                {
                    LogService.Warn(
                        "后台常驻调度已开启但未能启动（多半是已有其它实例在监听），" +
                        "详见日志页。");
                }
            }

            // 托盘图标常驻：关窗只是收起来，进程继续干活。
            // 总闸没开或用户关掉「显示托盘图标」时不建 —— 此时根本没有后台可言，
            // 建一个没有对应进程状态的图标只会误导。
            if (UiSettingsStore.Settings.BackendService
                && UiSettingsStore.Settings.ShowTrayIcon)
            {
                TrayIconService.EnsureCreated();
            }

            // 启动时自动应用已保存的调校。
            //
            // 这一步过去只在 -silent（开机计划任务）分支里做，用户双击图标正常启动
            // 时压根不写 SMU，于是设置页那个「自动应用调校」开关在最常见的使用
            // 方式下形同虚设 —— 表现就是「我明明开了开关，重启电脑后降压没了」。
            // 现在两条路径共用 TuningApplyService 的同一份写入实现。
            //
            // 放在后台线程：SMU 写入最坏要等 PawnIO 就绪 60 秒，不能拖住窗口出现。
            _ = System.Threading.Tasks.Task.Run(() => ApplySavedTuningOnStartup());

            LogService.Info("ZenForge 启动 · SMU 探测 + 后台调度");
        }

        /// <summary>
        /// 结束进程。所有退出路径都从这一个口子出去，避免漏掉收摊。
        /// </summary>
        public static void ExitApplication(int exitCode = 0)
        {
            if (_exiting)
                return;

            _exiting = true;
            AllowExit = true;

            HardStop();

            // 后台回退状态没进 Dispatcher 循环：Current 存在但没有可 Shutdown 的
            // Run 循环，直接 Set 信号让宿主线程接着走 Cleanup 并返回。
            System.Threading.ManualResetEventSlim? background = BackgroundExitSignal;
            if (background != null)
            {
                try { background.Set(); }
                catch { /* 已释放 */ }
                return;
            }

            try
            {
                Current?.Shutdown(exitCode);
            }
            catch (InvalidOperationException)
            {
                // 已经在关了。退出路径可能同时被好几处触发（托盘退出、窗口关闭、
                // 静默分支判定），第二次 Shutdown 是正常现象，不是错误。
            }
        }

        /// <summary>
        /// 把「WMI 降级提示」接到 UI 上。
        ///
        /// ProcessWatcher 是纯后台核心，不引用任何 WPF 类型，提示只能由这里注入。
        /// 纯后台路径不注入 —— 降级时它只记一行日志，绝不会弹出无人可关的窗口。
        /// </summary>
        private static void AttachProcessWatcherUi()
        {
            ProcessWatcher.WmiUnavailableNotifier = () =>
                Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!UiPresence.MainWindowVisible)
                    {
                        LogService.Warn(
                            "WMI 不可用，进程绑核已降级为定时轮询（主窗口不可见，未弹提示）");
                        return;
                    }

                    DarkDialog.ShowWarning(
                        "系统 WMI 服务不可用，进程自动绑核已降级为定时轮询模式。\n\n" +
                        "定时模式能正常工作，但会持续占用少量 CPU，且新进程的绑定会\n" +
                        "延迟几秒才生效。\n\n" +
                        "常见原因是系统精简版删除了 Windows Management Instrumentation\n" +
                        "（Winmgmt）服务。若希望恢复零开销的事件模式，可在「服务」中\n" +
                        "启用该服务后重启 ZenForge。",
                        "绑定监听降级");
                }));
        }

        /// <summary>
        /// 全局 UI 线程异常处理。两条启动路径（正常 / 从后台升级）共用这一份。
        ///
        /// 以前只在正常分支挂，升级分支漏了 —— 结果是升级后任何异常都表现为
        /// 「界面一闪就没了」，日志里只有一句 Dispatcher 栈，用户无从判断。
        /// </summary>
        private static void OnDispatcherUnhandledException(
            object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            // 先把栈落到磁盘再弹框：UI 线程已经出事了，弹框本身也可能失败
            LogService.Error($"未处理异常：{args.Exception}");

            try
            {
                DarkDialog.ShowError(
                    "发生了未预期的错误，详细信息已写入日志。\n\n" + args.Exception.Message,
                    "未处理异常");
            }
            catch
            {
                // 弹框失败不能让异常继续往上冒
            }

            args.Handled = true;
        }

        /// <summary>
        /// 停监听、停采样、收托盘。幂等，任何退出路径重复调用都安全。
        /// </summary>
        private static void HardStop()
        {
            Quietly(ProcessWatcher.Stop);
            Quietly(ProcessRefresher.Stop);
            Quietly(TrayIconService.Shutdown);

            // 唤醒宿主窗口跟着一起收：留着它等于留一个「进程已死但还能被 FindWindow 命中」
            // 的假地址，下一次双击会 PostMessage 到无效句柄，看起来像唤不醒。
            Quietly(WakeWindow.Destroy);

            // 日志是后台批量落盘的，退出前把最后几条冲掉
            Quietly(LogService.Flush);

            SingleInstance.Release();
        }

        /// <summary>退出阶段的收摊动作一律不许抛：前一步失败不能连累后面的清理。</summary>
        private static void Quietly(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 后台套用已保存调校，并把结果呈现给用户。
        /// 开关关闭 / 无保存配置时不打扰用户；写入失败则明确弹窗，不再静默。
        /// </summary>
        private static void ApplySavedTuningOnStartup()
        {
            TuningApplyOutcome outcome;
            try
            {
                outcome = TuningApplyService.ApplyFromStore();
            }
            catch (Exception ex)
            {
                LogService.Error($"启动应用调校未处理异常：{ex}");
                return;
            }

            if (outcome.Skipped)
            {
                LogService.Info("启动应用调校：" + outcome.SkipReason);
                return;
            }

            if (outcome.Success)
            {
                LogService.Info("启动应用调校：已写入并回读一致");
                return;
            }

            // 失败：等主窗口真正显示出来再弹，避免在还没有宿主窗口时弹出孤儿对话框。
            string message = string.IsNullOrWhiteSpace(outcome.Summary)
                ? "未知原因"
                : outcome.Summary;

            LogService.Error("启动应用调校失败：" + message);
            ShowWhenMainWindowReady("启动时自动应用调校失败：\n\n" + message +
                                    "\n\n已保存的配置没有被改动，可以在调校页手动点「应用」重试。",
                                    "启动应用调校失败");
        }

        /// <summary>
        /// 等主窗口就绪后弹提示。
        /// 后台任务可能比窗口创建更早结束，直接弹会得到一个没有宿主的孤儿对话框。
        /// </summary>
        private static void ShowWhenMainWindowReady(string message, string title, int attempt = 0)
        {
            Application? app = Current;
            if (app == null)
                return;

            Dispatcher ui = app.Dispatcher;

            ui.InvokeAsync(() =>
            {
                // 不能用 app.MainWindow：托盘菜单那个 1×1 宿主也是 Window，
                // 拿它当宿主会把对话框弹到屏幕外。
                Window? main = RealMainWindow;

                if (main != null && main.IsLoaded)
                {
                    Views.DarkDialog.ShowWarning(message, title);
                    return;
                }

                if (attempt >= 20)
                {
                    // 窗口迟迟没出来就不要再等了，日志里已经记过一次
                    LogService.Error($"启动应用调校失败提示未能显示（窗口就绪超时）：{title}");
                    return;
                }

                DispatcherTimer retry = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(300),
                    DispatcherPriority.Background,
                    delegate (object? sender, EventArgs args)
                    {
                        if (sender is DispatcherTimer self)
                            self.Stop();
                        ShowWhenMainWindowReady(message, title, attempt + 1);
                    },
                    ui);
                retry.Start();
            });
        }

        /// <summary>
        /// 挂唤醒回调。唤醒宿主窗口在后台常驻时已经建好（EnsureCreated 幂等，这里只是兜底），
        /// 通道不变、只换回调目标 —— 这样即使升级过程中丢了引用，下一次双击仍能找到同一个窗口。
        ///
        /// 命名事件通道作为兜底保留（同权限级时更省事），两路都指向同一个回调。
        /// </summary>
        private void AttachWakeChannel()
        {
            WakeWindow.EnsureCreated();
            WakeWindow.Activated = () =>
                Dispatcher.BeginInvoke(new Action(TrayIconService.ShowOrCreateMainWindow));

            SingleInstance.WakeRequested = () =>
                Dispatcher.BeginInvoke(new Action(TrayIconService.ShowOrCreateMainWindow));
        }

        /// <summary>读界面设置（主题 / 强调色）与调校 / 绑核 / 掩码配置，并应用主题。</summary>
        private static void LoadStores()
        {
            UiSettingsStore.Load();
            ThemeService.Initialize();
            ThemeService.StartSystemMonitor();

            TuningStore.Load();
            MaskStore.Load();
            BindingStore.Load();
        }

        /// <summary>命令行 / 环境变量里是否要求详细日志。解析失败一律当作不开启。</summary>
        private static void EnableVerboseLogIfRequested(string[]? args)
        {
            try
            {
                if (LogService.Verbose)
                    return;

                bool byArg = args != null && args.Any(a =>
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
        /// 运行时环境检测：确认当前运行于 .NET 10（框架依赖发布的前提）。
        /// 不满足时弹出深色提示框并准备退出。
        /// </summary>
        private static bool EnsureDotNetRuntime()
        {
            try
            {
                if (Environment.Version.Major < 10)
                {
                    const string msg =
                        "缺少 .NET 10 桌面运行时，无法启动 ZenForge。\n\n" +
                        "请前往官网下载并安装 .NET 10 桌面运行时：\n" +
                        "https://dotnet.microsoft.com/download/dotnet/10.0";
                    DarkDialog.ShowError(msg, "缺少运行时");
                    return false;
                }
            }
            catch
            {
                // 检测异常不阻断启动
            }

            return true;
        }

        /// <summary>把启动参数拼成可安全传给 ProcessStartInfo 的字符串（含空格参数加引号）。</summary>
        private static string BuildArgumentString(string[]? args)
        {
            if (args == null || args.Length == 0)
                return string.Empty;

            return string.Join(" ", args.Select(QuoteIfNeeded));
        }

        private static string QuoteIfNeeded(string arg) =>
            arg.Contains(' ') || arg.Contains('\t')
                ? "\"" + arg.Replace("\"", "\\\"") + "\""
                : arg;

        /// <summary>
        /// 判断是否为静默启动：命令行含 -silent / --silent / /silent（不区分大小写）。
        /// </summary>
        private static bool IsSilentLaunch(string[]? args)
        {
            return args != null && args.Any(arg =>
                arg.Trim().TrimStart('-', '/', '—').Equals("silent", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 全局干掉焦点虚线框（FocusVisualStyle）。
        ///
        /// 为什么不能只写 &lt;Style TargetType="Control"&gt;：
        /// WPF 的隐式样式按控件的 DefaultStyleKey **精确匹配类型**，不会沿继承链匹配到 Control，
        /// 所以 TargetType="Control" 的样式对 Button / TextBox / ComboBox 一律无效。
        /// 而显式引用具名样式的控件（Style="{StaticResource PrimaryButtonStyle}"）又完全不吃隐式样式。
        ///
        /// 因此这里在启动时遍历资源字典里的**所有 Style**（具名 + 隐式），统一追加
        /// FocusVisualStyle=null。此时页面尚未创建、Style 未被 Seal，可安全修改 Setters。
        /// </summary>
        private static void DisableFocusVisuals()
        {
            void Apply(ResourceDictionary dictionary, int depth)
            {
                if (depth > 8)
                    return; // 防御递归异常

                foreach (object? key in dictionary.Keys)
                {
                    // 取值本身就可能抛：某个 Style 的 BAML 写坏了（例如给只读属性写
                    // <Setter>），会在**枚举字典时**才真正解析并炸掉。0.8.4 的
                    // SettingsRowStyle 就是这么把整个启动流程带崩的 —— 一个装饰性
                    // 样式错误不该让程序打不开。
                    Style? style;
                    try
                    {
                        style = dictionary[key] as Style;
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"跳过无法解析的资源「{key}」：{ex.Message}");
                        continue;
                    }

                    if (style == null)
                        continue;
                    if (style.TargetType == null || !typeof(Control).IsAssignableFrom(style.TargetType))
                        continue;
                    if (style.Setters.OfType<Setter>()
                              .Any(s => s.Property == FrameworkElement.FocusVisualStyleProperty))
                        continue;

                    try
                    {
                        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
                    }
                    catch (InvalidOperationException)
                    {
                        // Style 已被使用而 Seal：跳过，主要控件在 XAML 里已单独处理
                    }
                    catch (Exception ex)
                    {
                        // 其它原因加不进去也不该中断启动
                        LogService.Warn($"样式「{key}」追加 FocusVisualStyle 失败：{ex.Message}");
                    }
                }

                // 递归处理嵌套的合并字典
                foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
                    Apply(merged, depth + 1);
            }

            if (Application.Current == null)
                return;

            Apply(Application.Current.Resources, 0);

            // 兜底：任何窗口（含 DarkDialog 这类独立 Window）出现后，都扫一遍可视树。
            // 注意：只扫 MainWindow 是不够的 —— DarkDialog 是独立 Window，
            // 它的「取消」按钮就因此一直留着系统默认的方框虚线。
            Application.Current.Activated += (_, _) => SweepAllWindows();
            Application.Current.Startup += (_, _) =>
            {
                // Startup 晚于 MainWindow 创建，此时挂 Loaded 是安全的
                foreach (Window window in Application.Current.Windows)
                    HookWindow(window);
            };

            // 【关键补丁：控件在 Activated 之后才创建，扫不到】
            //
            // 上面只在窗口 Activated / Loaded 时扫码。但四个页面由 Frame 导航切换，
            // 用户点「调度」「日志」「设置」时才实例化新页面的控件树 —— 这些控件
            // 在 Activated 那一刻还不存在，等它们挂上去就再也没人扫了。
            // 结果就是切换过页面之后，Tab 键又冒出系统虚线矩形。
            //
            // 两个兜底：
            //   1. 任何 FrameworkElement 一旦 Loaded，立刻清掉它自己的 FocusVisualStyle；
            //   2. 定时器低频补扫（400ms），兜住 Tab 焦点进入 ListViewItem
            //      这类由容器自动生成的元素。
            //
            // 不用 LayoutUpdated：它在滚动 / 鼠标移动时疯狂触发，
            // 每帧遍历整棵可视树会让界面明显卡顿。
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(delegate (object s, RoutedEventArgs e)
                {
                    if (s is FrameworkElement fe && fe.FocusVisualStyle != null)
                    {
                        try { fe.FocusVisualStyle = null; } catch { }
                    }
                }), true);

            // 键盘焦点落上去时补扫一次。
            //
            // 这里曾经挂着一个 400ms 的常驻定时器，每 2.5 秒遍历一次整棵可视树。
            // 程序改成常驻托盘之后，那个定时器会跟着进程跑一整天 —— 为了兜住
            // 「Tab 进入 ListViewItem」这种边角情况，让 UI 线程永远背着一份开销，不值。
            // Loaded 类处理器已经覆盖了所有元素（含虚拟化容器自动生成的那些），
            // 焦点事件再补一刀，剩下就交给这两条，不再轮询。
            EventManager.RegisterClassHandler(typeof(FrameworkElement), Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(delegate (object s, KeyboardFocusChangedEventArgs e)
                {
                    // 只扫焦点元素自己这一小棵，不碰整窗
                    if (s is FrameworkElement fe)
                    {
                        try { SweepVisualTree(fe); } catch { }
                    }
                }), true);
        }

        /// <summary>给窗口挂上 Loaded / 内容变更后的清扫钩子（幂等）。</summary>
        private static void HookWindow(Window window)
        {
            if (window == null || _hookedWindows.Contains(window))
                return;

            _hookedWindows.Add(window);

            // 弹窗每次都是新建的（DarkDialog 不复用实例），只加不减会把每一个弹窗
            // 连同它的整棵可视树永久攥在手里。关掉就从集合里摘掉。
            window.Closed += (_, _) => _hookedWindows.Remove(window);

            void Sweep()
            {
                try
                {
                    SweepVisualTree(window);
                }
                catch
                {
                    // 窗口可能正在关闭，忽略
                }
            }

            window.Loaded += (_, _) => Sweep();
            // 弹窗内容常由代码在 Loaded 之后才填充，这里再补一次
            window.ContentRendered += (_, _) => Sweep();
        }

        /// <summary>对当前所有窗口做一次清扫（新弹窗弹出时由 Activated 触发）。</summary>
        private static void SweepAllWindows()
        {
            if (Application.Current == null)
                return;

            foreach (Window window in Application.Current.Windows)
            {
                HookWindow(window);
                try
                {
                    SweepVisualTree(window);
                }
                catch
                {
                    // 忽略
                }
            }
        }

        /// <summary>已挂过钩子的窗口，避免重复订阅。</summary>
        private static readonly System.Collections.Generic.HashSet<Window> _hookedWindows = new();

        /// <summary>遍历可视树，把每个 FrameworkElement 的 FocusVisualStyle 置空。</summary>
        private static void SweepVisualTree(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element && element.FocusVisualStyle != null)
                {
                    try
                    {
                        element.FocusVisualStyle = null;
                    }
                    catch
                    {
                        // 个别元素只读，忽略
                    }
                }

                // Popup 的内容（如下拉框 / 列表项）在 IsOpen 后才进入可视树，
                // 普通递归扫不到。这里订阅 Opened，弹出时再补扫一次。
                if (child is Popup popup)
                {
                    try
                    {
                        popup.FocusVisualStyle = null;
                    }
                    catch
                    {
                        // 只读，忽略
                    }

                    popup.Opened -= SweepPopupContent;
                    popup.Opened += SweepPopupContent;
                }

                SweepVisualTree(child);
            }
        }

        /// <summary>Popup 弹出时清扫其弹出内容（下拉框 / 列表项等系统虚线）。</summary>
        private static void SweepPopupContent(object? sender, EventArgs e)
        {
            if (sender is not Popup popup || popup.Child == null)
                return;

            try
            {
                SweepVisualTree(popup.Child);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 当前进程是否已具备管理员权限。
        /// 方案 A 下清单为 asInvoker，所有提权判断都集中在这里。
        /// </summary>
        private static bool IsElevated()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // 身份查询失败时按未提权处理
                return false;
            }
        }

        /// <summary>返回 true = 已具备管理员权限（可继续启动）；false = 已触发提权重启，本实例应退出。</summary>
        /// <param name="args">原始启动参数，提权重启时原样透传（保持 -silent 等语义）。</param>
        private static bool EnsureAdministrator(string[]? args = null)
        {
            if (IsElevated())
            {
                LogService.Info("已获得管理员权限，SMU 只读探测可用");
                return true;
            }

            LogService.Warn("未以管理员身份运行，尝试提权重启…");

            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    LogService.Error("无法定位当前可执行文件，跳过提权重启");
                    return true;
                }

                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    // 透传原始参数，避免提权后丢掉 -silent 而弹出主窗口
                    Arguments = BuildArgumentString(args)
                });

                LogService.Info("已请求提权重启，当前实例即将退出");
                return false;
            }
            catch (Win32Exception)
            {
                // 用户在 UAC 对话框中拒绝：以普通权限继续，SMU 读取会失败并给出友好提示
                LogService.Warn("用户取消了 UAC 提权，将以普通权限继续运行（SMU 读取可能失败）");
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error($"提权重启失败: {ex.Message}");
                return true;
            }
        }
    }
}
