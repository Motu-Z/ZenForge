using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Zentwik.Helpers;
using Zentwik.Services;
using Zentwik.Views;

namespace Zentwik
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
        /// 单实例锁、ProcessWatcher、管理员权限都已由后台宿主持有，这里若再走一遍
        /// TrySignalExisting / TryAcquire / EnsureAdministrator，轻则多弹一次 UAC，
        /// 重则 runas 重启把锁和监听全丢。
        /// </summary>
        public static bool IsHeadlessUpgrade;

        /// <summary>退出流程是否已经走过。Shutdown 只能调一次，重复调用会抛异常。</summary>
        private static bool _exiting;

        /// <summary>
        /// 「后台回退」状态下的退出信号：后台宿主升级界面失败，WPF 已加载但界面没建起来，
        /// 进程退回到「有托盘、无主窗口」的常驻状态等在这里。
        ///
        /// 此时 <see cref="ExitApplication"/> 不能只调 Application.Shutdown —— 那条路径
        /// 要求 Application 已经 Run 起来，而回退状态根本没进 Dispatcher 循环。
        /// 由宿主把这个信号交出来，退出时直接 Set 放走它。
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
        /// 真正的主窗口，不能用 Application.MainWindow 代替：
        ///   · 托盘菜单宿主是第一个被 Show 出来的 Window，MainWindow 会指到它身上，
        ///     拿它当 Owner 弹窗会跑到屏幕 (-10000,-10000) 去；
        ///   · 轻量模式「仅留服务进程」会把主窗口整个销毁，此时 MainWindow 指向的是一个
        ///     已经 Closed 的残骸，谁用它谁炸。
        /// 所以实例由 App 自己持有，创建与销毁走 <see cref="SetMainWindow"/> /
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

                // 补上 UI 线程之外的两条通道（AppDomain / 未观察 Task）。
                // 原先只有上面这一条，后台采样线程出事时日志里一片空白。
                AttachGlobalExceptionHandlers();

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

                LogService.Info("升级完成 · Zentwik 界面已从后台宿主加载（未重新抢锁、未重复写 SMU）");
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

            _startupArgs = e.Args;

            // ==================== 单实例：先探测 ====================
            // 必须在 base.OnStartup 之前：基类会按 StartupUri 把主窗口造出来并显示，
            // 之后再判断就会先闪一下窗口、白跑一遍页面布局，然后才退出。
            //
            // 放在提权之前同理：已有实例时直接唤醒它并退出，连 UAC 都不该弹。
            bool silent = IsSilentLaunch(e.Args);

            if (SingleInstance.TrySignalExisting(signalMain: !silent))
            {
                LogService.Info(silent
                    ? "已有 Zentwik 实例在运行，静默实例退出（不打扰已有实例）"
                    : "已有 Zentwik 实例在运行，本实例已请求其唤出主窗口并退出");

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

            LogService.Info($"Zentwik 启动 · 详细日志：{(LogService.Verbose ? "已开启" : "关闭")}"
                + $" · 版本 {VersionText}");

            // ==================== .NET 10 桌面运行时检测 ====================
            // 本程序为「框架依赖」发布，不自带 .NET 运行时。
            // 用户直接双击 Zentwik.exe 运行时，apphost 会先拦截低版本运行时；
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
            Zentwik.Core.Services.ProcessRefresher.Start();

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
                // 若是用户手动 Zentwik.exe -silent 且未提权，则只记日志、**绝不弹 UAC**
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
                    LogService.Info("静默模式：已有 Zentwik 实例在运行，本实例退出");
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
                LogService.Info("已有 Zentwik 实例在运行，本实例退出（已请求其唤出主窗口）");
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

            // 补上 UI 线程之外的两条通道（AppDomain / 未观察 Task）。
            AttachGlobalExceptionHandlers();

            // 日志服务在 UI 线程上下文中注册，后台线程写日志时自动封送回 UI 线程
            LogService.UiContext = SynchronizationContext.Current;

            // 场景自动切换的调用方是后台的进程 / 电源事件线程，同样需要回到 UI 线程
            // 才能动方案集合（v1.1.x 接入事件源后生效）。
            ScenarioService.UiContext = SynchronizationContext.Current;

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

            LogService.Info("Zentwik 启动 · SMU 探测 + 后台调度");
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

                    LogService.Warn(
                        "系统 WMI 服务不可用，进程绑核已降级为定时轮询（会持续占用少量 CPU，新进程绑定延迟数秒）");
                    ToastService.Warning(
                        "WMI 服务不可用，进程绑核已降级为定时轮询（新进程绑定会延迟数秒，详见日志页）");
                }));
        }

        /// <summary>
        /// 全局 UI 线程异常处理。两条启动路径（正常 / 从后台升级）共用这一份。
        ///
        /// 以前只在正常分支挂，升级分支漏了 —— 结果是升级后任何异常都表现为
        /// 「界面一闪就没了」，日志里只有一句 Dispatcher 栈，用户无从判断。
        ///
        /// ============================================================
        /// 【1.0.0-can9 的「整机卡死 + 2772 条 FATAL + OOM」就是这里造成的】
        /// ============================================================
        /// 旧逻辑是：记日志 → 在闸门内**同步 ShowDialog** → finally 把闸门放回 0。
        ///
        /// 一旦某个异常是「持续性」的（当时是控件模板里找不到资源，每次 ApplyTemplate
        /// 都会再抛一次），就变成：
        ///
        ///     Layout 抛异常 → 弹模态框（占住 UI 线程）→ 用户只能看着
        ///     → 关框 / 框自身失败 → finally 放开闸门 → 下一帧 Layout 又抛
        ///     → 再弹 …… 每轮都新建一个 Window 对象
        ///
        /// 日志里 20:28:37 那一秒内出现上千条记录、最后 OOM，是这条环路在疯狂重入：
        /// 每次重入都新建窗口、都写一遍完整堆栈、都尝试拉起一个模态消息泵。
        /// 用户看到的就是「进程页白了 + 整个软件卡死」。
        ///
        /// 三条修正，缺一不可：
        ///   1. **连续异常不再弹框**：同一个异常在短时间内反复出现，说明界面已经
        ///      处于坏状态，弹框既帮不上忙又可能自己也是坏的。只记一次日志。
        ///   2. **闸门只在「弹框成功」后放开**：弹框自身失败说明 UI 已经不可用，
        ///      此后再也不弹 —— 这正是旧代码 finally 无条件放开留下的口子。
        ///   3. **每秒最多一个弹框**：即便异常各不相同，也不允许高频弹窗。
        /// </summary>
        private static void OnDispatcherUnhandledException(
            object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            HandleUiException(args.Exception);
            args.Handled = true;
        }

        /// <summary>同类异常连续出现这么多次后，不再为它弹框。</summary>
        private const int RepeatedExceptionLimit = 3;

        /// <summary>两次弹框之间的最小间隔（毫秒）。</summary>
        private const int DialogCooldownMs = 1000;

        /// <summary>同一签名异常的弹框次数上限。超了改走 Toast。</summary>
        private const int SameSignatureDialogLimit = 1;

        /// <summary>限流表最多记多少个签名，超了整表清空（与 BumpSignatureTotal 同一口径）。</summary>
        private const int MaxTrackedSignatures = 256;

        /// <summary>
        /// 同签名异常每累计这么多次，补一行计数日志。
        ///
        /// 500 是折中：一次真实风暴动辄上千次，报告「第 500 / 1000 / 1500 次」
        /// 足够让人判断规模；再密就回到写盘把 UI 线程拖死的老路上。
        /// </summary>
        private const int LogEveryNSameSignature = 500;

        /// <summary>同签名异常的累计次数（不随「是否连续」清零，用于日志限流）。</summary>
        private static readonly Dictionary<string, int> _signatureTotal = new(StringComparer.Ordinal);

        /// <summary>
        /// 累加某个签名的出现次数。
        ///
        /// 加锁而不是用 Interlocked：字典本身不是线程安全的，而 Dispatcher 异常
        /// 与后台线程异常（AppDomain 通道）可能同时进来。
        /// 上限 256 个签名后整表清空 —— 这个表只服务于限流，清空最多让某些
        /// 罕见的签名重新享受「前 3 次写完整堆栈」的待遇，不影响正确性。
        /// </summary>
        private static int BumpSignatureTotal(string signature)
        {
            lock (_signatureTotal)
            {
                if (_signatureTotal.Count > MaxTrackedSignatures)
                    _signatureTotal.Clear();

                _signatureTotal.TryGetValue(signature, out int total);
                _signatureTotal[signature] = ++total;
                return total;
            }
        }

        private static string _lastExceptionSignature = string.Empty;
        private static int _repeatedCount;
        private static long _lastDialogTick;
        private static int _dialogBroken;

        // 同一签名的异常已经弹过几次框（超限后只 Toast / 只记日志）
        private static readonly Dictionary<string, int> _signatureDialogCount = new(StringComparer.Ordinal);

        // Toast 通道被判定坏掉后永久停用（0 = 可用，1 = 已坏）
        private static int _toastBroken;

        /// <summary>
        /// UI 线程未处理异常的统一出口。策略是**绝不悄悄退出**，按成本从低到高逐级降级：
        ///
        ///     1. 先落盘（Critical + 完整堆栈）—— 这一步几乎不可能失败；
        ///     2. 尝试 Toast（非阻断，3 秒自散，不占 UI 线程）；
        ///     3. Toast 用不了再尝试模态框（用户必须看到，但会占住 UI 线程）；
        ///     4. 两者都用不了时，彻底静音，但继续记日志 —— 界面还能动就让它动。
        ///
        /// 全程不调 Shutdown：一个渲染异常不该等于「程序自己关了」。用户察觉不到
        /// 界面问题时至少还能从托盘退出，而不是对着一个凭空消失的进程发火。
        ///
        /// 三条防风暴措施缺一不可（1.0.0-can9 整机卡死就是缺了它们）：
        ///   · 同一签名（类型 + 最内层消息）连续出现超过 3 次 → 只记日志；
        ///   · 同一签名最多弹 1 次模态框，之后改 Toast；
        ///   · 两次模态框最小间隔 1000 ms，且弹框自身失败后永久停用模态框。
        /// </summary>
        private static void HandleUiException(Exception exception)
        {
            // ---------- 1. 识别「同一个异常在刷屏」 ----------
            // 只看类型 + 最内层消息：持续性 bug（模板找不到资源、布局死循环、
            // 父级类型不对）的特征就是这两者恒定不变，而栈每帧都不同。
            string signature = exception.GetType().FullName + "|" + InnermostMessage(exception);
            int total = BumpSignatureTotal(signature);

            int seen;
            if (signature == _lastExceptionSignature)
            {
                seen = ++_repeatedCount;
            }
            else
            {
                _lastExceptionSignature = signature;
                seen = _repeatedCount = 1;
            }

            // ---------- 2. 落盘（按签名限流）----------
            // 【为什么不能无条件写完整堆栈】
            // 一次布局风暴一秒能抛上千次：单条 FATAL 带堆栈约 4KB，一秒就是几兆，
            // 而写盘本身就发生在 UI 线程上 —— 日志从几兆涨到几百兆的速度
            // 会超过界面自救的速度（1.0.2 实测「几秒 500MB + 整机卡死」）。
            //
            // 现在：同签名前 3 次写完整堆栈（第一次的栈就是根因，信息量不丢），
            // 之后每 500 次补一行计数。磁盘占用被锁在一个常数级，
            // 而「发生了多少万次」这个事实仍然留在日志里。
            try
            {
                if (total <= RepeatedExceptionLimit)
                    LogService.Critical($"未处理异常（第 {total} 次）：{exception}");
                else if (total % LogEveryNSameSignature == 0)
                    LogService.Critical($"未处理异常持续发生（同签名累计 {total} 次，已省略堆栈）");
            }
            catch
            {
                // 连日志都写不了，只能放弃记录，但仍要继续尝试提示
            }

            if (seen > RepeatedExceptionLimit)
            {
                // 已经提示（或本该提示）不少于 3 次还在继续：界面已处于坏状态，
                // 再提示只是把用户锁死在对话框里，对定位没有任何增量信息。
                if (seen == RepeatedExceptionLimit + 1)
                {
                    LogService.Critical(
                        $"同一异常已连续出现 {RepeatedExceptionLimit} 次以上，" +
                        "后续同类异常只记日志、不再提示（界面可能已处于不可用状态）。");
                }
                return;
            }

            // ---------- 3. 低频异常：先 Toast ----------
            // Toast 不占 UI 线程、不建新窗口、失败只是静默返回，是这里最安全的
            // 提示通道，所以放在模态框**前面**。模态框只留给「Toast 不可用」的场合。
            if (Volatile.Read(ref _toastBroken) == 0 && TryToastException(exception))
                return;

            // ---------- 4. 模态框：同一签名只弹一次，且有频率上限 ----------
            // 【为什么要给这个表也设上限】
            // 签名是「类型 + 最内层消息」，而消息里常带变化的值（进程名、路径、句柄号），
            // 理论上是无界的。_signatureTotal 有 256 条后清空的兜底，这张表原来没有 ——
            // 一个长期运行的进程攒下几万条签名，就是一条谁也不会去查的慢泄漏。
            // 清空最多让某个老签名重新获得一次弹框机会，不影响任何限流语义。
            if (_signatureDialogCount.Count > MaxTrackedSignatures)
                _signatureDialogCount.Clear();

            _signatureDialogCount.TryGetValue(signature, out int shown);
            if (shown >= SameSignatureDialogLimit)
                return;

            if (Volatile.Read(ref _dialogBroken) != 0)
                return;

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastDialogTick) < DialogCooldownMs)
                return;

            // 闸门：防止「弹框过程中又进异常」的重入（递归会 StackOverflow）。
            if (Interlocked.Exchange(ref _dialogFailureGuard, 1) != 0)
                return;

            Interlocked.Exchange(ref _lastDialogTick, now);
            _signatureDialogCount[signature] = shown + 1;
            bool ok = false;
            try
            {
                DarkDialog.ShowError(
                    "发生了未预期的错误，详细信息已写入日志。\n\n" + exception.Message,
                    "未处理异常");
                ok = true;
            }
            catch (Exception dialogError)
            {
                // 弹框都失败了，说明可视树 / 主题字典已经不可用。
                // 永久关闭弹框通道 —— 旧代码在 finally 里无条件放开闸门，
                // 于是「弹框失败 → 下一帧再弹 → 再失败」可以无限循环。
                Volatile.Write(ref _dialogBroken, 1);
                LogService.Critical($"异常提示框自身失败，已停止弹框（后续只记日志）：{dialogError}");
            }
            finally
            {
                if (ok)
                    Interlocked.Exchange(ref _dialogFailureGuard, 0);
            }
        }

        /// <summary>
        /// 用 Toast 报一次未处理异常。返回 false 表示 Toast 通道不可用，
        /// 调用方应继续尝试更重的提示手段。
        ///
        /// 单独抽出来是因为这里必须**先判断可用性再调用**：ToastService.Show 自己
        /// 会吞掉异常（那是它该做的），但调用方需要知道「到底显没显出来」。
        /// </summary>
        private static bool TryToastException(Exception exception)
        {
            try
            {
                if (ToastService.Host == null)
                    return false;         // 宿主没挂（无窗口 / 启动早期），换模态框

                // 一行能读完的摘要：异常类型 + 最内层消息。完整堆栈在日志里。
                string summary = exception.GetType().Name + "：" + InnermostMessage(exception);
                if (summary.Length > 120)
                    summary = summary[..120] + "…";

                ToastService.Error("发生未预期的错误，已写入日志 —— " + summary);
                return true;
            }
            catch (Exception toastError)
            {
                Volatile.Write(ref _toastBroken, 1);
                try
                {
                    LogService.Critical($"异常 Toast 自身失败，已停用 Toast 提示：{toastError}");
                }
                catch
                {
                    // 忽略
                }

                return false;
            }
        }

        /// <summary>取最内层异常的消息：外层是包装异常时，外层消息往往是同一句废话。</summary>
        private static string InnermostMessage(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null)
                current = current.InnerException;
            return current.Message;
        }

        /// <summary>
        /// 弹窗重入闸门：0 = 空闲，1 = 正在弹。
        /// 用 Interlocked 而不是 bool，因为多个 Dispatcher 帧可能交错进入。
        /// 注意：只有弹框**成功**才放回 0，见 HandleUiException。
        /// </summary>
        private static int _dialogFailureGuard;

        /// <summary>
        /// 非 UI 线程的未处理异常。
        ///
        /// 这条分支一旦进来，CLR 已经开始拆 AppDomain，进程必定终止 ——
        /// 此时**任何 UI 操作都不安全**（Dispatcher 可能已停、消息泵已退），
        /// 所以只做一件事：把异常落到日志。绝不能在这里弹框，那会把
        /// 「一个可诊断的崩溃」变成「一个静默消失的进程」。
        /// </summary>
        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                string stage = e.IsTerminating ? "进程即将终止" : "进程继续运行";

                // 与 UI 线程同一套限流：后台循环里的一次风暴同样能在几秒内
                // 把日志写到几百兆，而那时候磁盘写入又会拖慢整个进程。
                Exception? ex = e.ExceptionObject as Exception;
                string signature = "Domain|" + (ex?.GetType().FullName ?? "unknown")
                                   + "|" + (ex != null ? InnermostMessage(ex) : string.Empty);
                int total = BumpSignatureTotal(signature);

                if (total <= RepeatedExceptionLimit)
                    LogService.Critical($"Domain 未处理异常（{stage}）：{e.ExceptionObject}");
                else if (total % LogEveryNSameSignature == 0)
                    LogService.Critical($"Domain 未处理异常持续发生（{stage}，同签名累计 {total} 次，已省略堆栈）");

                // 后台线程的异常往往发生在采样 / 监听循环里，
                // 最后几条日志可能还在缓冲区，走之前强推一次。
                LogService.Flush();
            }
            catch
            {
                // 崩溃路径上不允许再抛
            }
        }

        /// <summary>
        /// 未被 await / Wait 的 Task 异常。
        ///
        /// 这类异常在 .NET 4.5+ 默认不会杀进程，但会在 GC 回收 Task 时
        /// 静默吞掉 —— 表现为「某个功能突然不工作了，日志里什么都没有」。
        /// 标记 SetObserved 是明确表态「我已处理」，否则 UI 线程之外
        /// 的这一跳可能被 CLR 升级成致命错误。
        /// </summary>
        private static void OnUnobservedTaskException(
            object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                string signature = "Task|" + e.Exception.GetType().FullName
                                   + "|" + InnermostMessage(e.Exception);
                int total = BumpSignatureTotal(signature);

                if (total <= RepeatedExceptionLimit)
                    LogService.Critical($"未观察的 Task 异常：{e.Exception}");
                else if (total % LogEveryNSameSignature == 0)
                    LogService.Critical($"未观察的 Task 异常持续发生（同签名累计 {total} 次，已省略堆栈）");
            }
            catch
            {
                // 同上
            }

            e.SetObserved();
        }

        /// <summary>
        /// 挂上三条异常通道。两条启动路径（正常 / 从后台升级）都要调。
        ///
        /// DispatcherUnhandledException 只覆盖 UI 线程；后台采样线程、线程池、
        /// Task 这三条路以前是裸奔的 —— 出事时日志里干干净净，用户只看到闪退。
        /// 方法做成幂等，重复调用不会重复挂。
        /// </summary>
        private static void AttachGlobalExceptionHandlers()
        {
            if (Interlocked.Exchange(ref _handlersAttached, 1) != 0)
                return;

            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>异常通道是否已挂（1 = 已挂）。</summary>
        private static int _handlersAttached;

        /// <summary>本次启动的原始命令行参数，供延迟到 store 加载之后的自检使用。</summary>
        private static string[]? _startupArgs;

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
            // 失败原因写进日志，Toast 只给一句能看懂的话 + 下一步动作，
            // 不把整段 outcome.Summary 塞进一行气泡里。
            ShowWhenMainWindowReady(
                "启动时自动应用调校失败，配置未被改动，可在调校页手动重试",
                "启动应用调校失败：" + message);
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
                    // 标题只用来进日志，气泡里只放一句话 —— 长文本塞进 Toast
                    // 会被截断，而这里的内容本来就该在日志里查。
                    LogService.Warn($"{title}");
                    Services.ToastService.Warning(message);
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

            // 方案与场景规则也要在启动时读进来：场景自动切换可能在界面还没打开
            // 的时候就被后台事件触发，那时再读盘就晚了（规则还没进内存）。
            ProfileService.Load();
            ScenarioService.Load();

            // 规则读进来之后才决定监听什么：需要监听哪些输入源取决于规则内容
            // （只配了电源规则就不该挂进程事件）。Sync 内部幂等，重复调用安全。
            ScenarioMonitor.Sync();

            RunSelfCheckIfRequested();
        }

        /// <summary>命令行 / 环境变量里是否要求详细日志。解析失败一律当作不开启。</summary>
        /// <summary>
        /// `-selftest` 时跑一遍场景自动切换的底层自检。
        ///
        /// 放在 store 与规则都加载完成之后：自检要读真实的配置才能验证
        /// 「监听范围与规则内容一致」这类断言，加载之前读到的全是空。
        ///
        /// 自检不下发任何硬件（见 ScenarioSelfCheck 的类注释），因此可以安全地在
        /// 正常启动时跑 —— 它只是多写十几行日志。
        /// </summary>
        private static void RunSelfCheckIfRequested()
        {
            try
            {
                string[]? args = _startupArgs;
                bool requested = args != null && args.Any(a =>
                    string.Equals(a, "-selftest", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));

                if (!requested)
                    return;

                ScenarioSelfCheck.Run();
            }
            catch (Exception ex)
            {
                // 自检自己出问题绝不能影响启动
                LogService.Warn($"场景自检执行失败：{ex.Message}");
            }
        }

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
                        "缺少 .NET 10 桌面运行时，无法启动 Zentwik。\n\n" +
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
        /// 不能只写一份 &lt;Style TargetType="Control"&gt;：WPF 隐式样式按 DefaultStyleKey
        /// 精确匹配类型，不会沿继承链命中 Control，对 Button / TextBox / ComboBox 一律无效；
        /// 而显式引用具名样式的控件又完全不吃隐式样式。
        /// 所以启动时遍历资源字典里的所有 Style（具名 + 隐式），统一追加
        /// FocusVisualStyle=null —— 此时页面尚未创建、Style 未被 Seal，可安全改 Setters。
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

            // 控件在窗口 Activated 之后才创建，那时的扫描覆盖不到它们。
            // 四个页面由 Frame 导航切换，用户点「调度」「日志」「设置」时才实例化新的
            // 控件树 —— 等它们挂上去就再也没人扫了，切换过页面后 Tab 又冒出系统虚线。
            // 兜底：任何 FrameworkElement 一旦 Loaded 就清掉自己的 FocusVisualStyle。
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
            // 这里曾经挂着一个常驻定时器每 2.5 秒遍历整棵可视树。程序改成常驻托盘后
            // 它会跟着进程跑一整天 —— 为兜住「Tab 进入 ListViewItem」这种边角情况让
            // UI 线程永远背一份开销，不值。Loaded 类处理器已覆盖所有元素（含虚拟化
            // 容器自动生成的），焦点事件再补一刀即可，不再轮询。
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
