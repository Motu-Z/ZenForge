# S4 阶段交接与 S5 UI 开发指南

> 基线：main @ `32cf9e7`（文档提交；最后功能性提交 `4c86490` fix(di)）· Tag `v2.0.0-can3` → `4c86490`
> 受众：接手 S5（WinUI 3 UI 实装）的开发者。本文只讲核心约束与排雷结论，不重复已封版设计文档。
> 配套详阅：`docs/全局交接文档.md`、`docs/UI-设计规范.md`、`docs/WPF封版与WinUI3重构交接文档.md`。

---

## 1. 当前代码基线

### 1.1 版本与提交
- main 最新 commit：`32cf9e7`（docs: 合入更新日志）。最后功能性提交：`4c86490`（fix(di): 补齐 LogService/AppPaths 具体类型注册，修复启动秒崩）。
- 最新 Tag：`v2.0.0-can3`（指向 `4c86490`）。

### 1.2 分层项目结构
```
Zentwik.slnx
├─ Zentwik.Core/            ← 纯逻辑层（AOT 已验证，零 UI 框架依赖）
│   ├─ Services/            ← 业务服务（ISmuReader/SmuReader、IProcessWatcher/ProcessWatcher、
│   │                         TuningApplyService、ProfileService、Scenario* …）
│   ├─ Helpers/             ← AppPaths、StartupCrashReporter、LogThrottle
│   ├─ Threading/           ← IUiDispatcher、DelegatingUiDispatcher
│   └─ DependencyInjection/ ← ServiceCollectionExtensions（注册入口 AddZentwikCore）
├─ Zentwik.csproj           ← WPF 过渡线（S6 下线），含 Views/Controls/Themes/Services(Zentwik.Compat.Svc 桥)
├─ Zentwik.App/             ← WinUI 3 外壳（S4 新建）
│   ├─ Program.cs           ← 自定义 Main（DISABLE_XAML_GENERATED_MAIN）
│   ├─ App.xaml(.cs)        ← 生命周期 + 挂载真实调度器
│   ├─ MainWindow.xaml(.cs) ← Mica + 原生标题栏
│   ├─ Services/AppHost.cs  ← 组合根（WinUI）
│   └─ app.manifest         ← requireAdministrator
└─ build/Zentwik.AotProbe/  ← AOT 门禁（dotnet publish -p:PublishAot=true -warnaserror）
```

**红线**：Core 不引用 WPF / WinUI / WindowsBase；Core 只引 `Microsoft.Extensions.DependencyInjection.Abstractions`（不含 Hosting）。容器由宿主创建，Core 只负责「把服务按什么生命周期登记进去」。

---

## 2. 依赖注入规范与排雷

### 2.1 注册入口位置
- 唯一登记处：`Zentwik.Core/DependencyInjection/ServiceCollectionExtensions.AddZentwikCore(this IServiceCollection, IUiDispatcher? dispatcher = null, Action<LogOptions>? configureLog = null)`。
- 宿主组合根：WinUI 走 `Zentwik.App/Services/AppHost.Build()`；WPF 走 `Services/Composition.Build()`。二者都只 `new ServiceCollection()` → `AddZentwikCore(...)` → `BuildServiceProvider()`。Core 不拥有容器实现。

### 2.2 S2 深坑：只注册接口、未注册具体类型
**现象**：双击 `Zentwik.exe` 无窗口直接退出，`0xe0434352`，事件查看器无堆栈。真实异常在「Windows 日志 → 应用程序 → 来源 .NET Runtime → 事件 1026」：
```
System.InvalidOperationException:
  No service for type 'Zentwik.Core.Services.LogService' has been registered.
   at Microsoft.Extensions.DependencyInjection...GetRequiredService[T](...)
   at Zentwik.App.OnStartup(...)
```
**根因**：WPF 过渡桥 `Zentwik.Compat.Svc`（`Services/AppServices.cs`）与 `HeadlessHost` 都按**具体类型**解析：
```csharp
internal static LogService Log   => Get<LogService>();   // Svc
internal static AppPaths  Paths  => Get<AppPaths>();
```
若注册写成只登记接口：
```csharp
services.TryAddSingleton<ILogService>(p => new LogService(...));   // ❌ 具体类型不在容器里
```
`GetRequiredService<LogService>()` 必然抛 —— 且 `--silent` 自启路径同样崩（更隐蔽，表现为绑核从未生效）。

**正确写法（每个服务两份注册，映射到同一实例）**：
```csharp
// 具体类型
services.TryAddSingleton<LogService>(provider =>
{
    LogOptions options = new();
    configureLog?.Invoke(options);
    if (string.IsNullOrWhiteSpace(options.Directory))
        options.Directory = provider.GetRequiredService<IAppPaths>().Logs;
    return new LogService(options, dispatcher);
});
// 接口映射到同一实例
services.TryAddSingleton<ILogService>(p => p.GetRequiredService<LogService>());
```
已落地于 `ServiceCollectionExtensions.cs`（AppPaths / LogService / 各 Store / SmuReader / ProcessWatcher 均如此）。

**铁律**：往 Core 加任何有状态服务，**必须同时登记具体类型 + 接口映射**。`Svc` 是定时炸弹——S6 下线 WPF 时它一并消失；在那之前，漏一处映射 = 一次启动崩溃。无状态工具类（`JsonFile` 等）保持 `static`，不强求 DI。

### 2.3 DelegatingUiDispatcher 初始化
- 作用：`IUiDispatcher` 的可延后挂载实现（`Zentwik.Core/Threading/`）。**未挂载时 `Post(action)` 直接就地执行** —— 恰好覆盖 `--silent` 后台模式（日志就地写，不依赖 UI 线程）；挂载后转发到 UI 线程。
- 宿主持有单例：`AppHost.Dispatcher` / `Composition.Dispatcher` 都是 `new DelegatingUiDispatcher()`。
- 构建时传入：`services.AddZentwikCore(Dispatcher)` → 内部 `if (dispatcher is not null) services.TryAddSingleton(dispatcher);` 注册同一实例。
- 真实调度器在 UI 框架就绪后挂载：
  - WinUI：`App.OnLaunched` 里 `AppHost.Dispatcher.Attach(new SynchronizationContextDispatcher(context))`，`context` 来自 `DispatcherQueue.AsSynchronizationContext()`。
  - WPF：`DispatcherSynchronizationContext`。
- 时序：`Main` 先 `AppHost.Build()`（DelegatingUiDispatcher 未挂载）→ `App.OnLaunched` 挂载真实调度器 → 之后日志 / UI 回调走 UI 线程。
- 红线：`IUiDispatcher` 抽象切断了 Core 对 `DispatcherQueue` / WPF `Dispatcher` 的依赖；Core 永远不认识任何 UI 框架类型。

---

## 3. WinUI 3 启动与窗口生命周期排雷

### 3.1 自定义 Main（silent 改道）
- `Zentwik.App.csproj` 设 `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>`，`Program.Main` 自写。
- `--silent` 判定必须在**任何 WinUI 类型被 JIT 解析之前**：Main 里不出现 `new App()`，创建动作塞进 `Application.Start` 的 lambda（lambda 体在判定后才被调用）。
- `WinRT.ComWrappersSupport.InitializeComWrappers();` 必须在 `Application.Start` 之前，否则首个 WinRT 调用以 `InvalidCastException` 炸在与真因无关处。

### 3.2 启动级异常捕获（两处接入）
| 接入点 | 覆盖的崩溃 | 说明 |
|---|---|---|
| `Program.Main` 最外层 try-catch | 容器构建 / 路径迁移 / 原生库加载 / `new App()` 构造期 | `StartupCrashReporter.Report(ex, interactive: !silent)` 后 **再 throw**（保留原退出码）。落盘路径 `%AppData%\Zentwik\startup_crash.log`。 |
| `App.UnhandledException` | XAML 失败（stowed exception） | `StartupCrashReporter.Report(e.Exception, interactive: false)`。**唯一**能抓 XAML 构造期崩溃的通道。 |

> **stowed exception**：WinUI 把 XAML 构造期的异常转成 `0xc000027b` 终止进程，事件查看器只显示 `Microsoft.UI.Xaml.dll 出错` + 无堆栈，**不走** Main 的 catch。所以 `Application.UnhandledException` 必须接，否则 XAML 期崩溃会成为「双击没反应、查不到原因」的死局。（`StartupCrashReporter` 刻意不依赖 `ILogService` / `IAppPaths` —— 崩溃点常就在它们内部。）

### 3.3 PreferredHeightOption 顺序坑（MainWindow 构造函数）
- ❌ 错误：`AppWindow.TitleBar.PreferredHeightOption = ...` 写在 `ExtendsContentIntoTitleBar = true` 之前 → 抛 `COMException 0x8007139F`（组或资源的状态不是执行请求操作的正确状态）。
- ✅ 正确顺序（硬约束，不可调换）：
  ```csharp
  ExtendsContentIntoTitleBar = true;
  SetTitleBar(AppTitleBar);
  AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
  ```
- 表象：窗口未出现进程就没了，事件查看器给 `Microsoft.UI.Xaml.dll + 0xc000027b`（与 3.2 stowed exception 同表象，根因不同，别混淆）。

### 3.4 Mica 与标题栏配色
- 根 `Grid` **不设** `Background`，否则 Mica 看不见。
- 先用 `MicaController.IsSupported()` 探测；不支持降级 `DesktopAcrylicBackdrop` / 纯色（直接 `new MicaBackdrop()` 在不支持机器上不抛但给死板纯色）。
- 标题栏按钮配色走 `AppWindow.TitleBar`（**非 XAML 通道**），须在 `ActualThemeChanged` 时重设。

---

## 4. S5 UI 开发指南

### 4.1 全新编写，零 WPF 包袱
- WPF 的 `Views/`、`Controls/`、`Themes/`、`Zentwik.Compat.Svc` 桥一律不复用，S6 整体下线。新页面 = WinUI 3 XAML + `x:Bind` + CommunityToolkit.Mvvm（`ObservableObject` / `[ObservableProperty]` / `RelayCommand`）。
- 禁止继承 WPF 习惯：`StaticResource` 跨字典引用、`WindowStyle=None` + `DragMove` 无边框、`Page` 导航语义等。

### 4.2 ViewModel 构造函数注入 Core 服务
- 页面 ViewModel 在构造参数里声明所需 Core 接口，由 DI 解析。**绝不用 `Svc`**。
  ```csharp
  public partial class TuningViewModel : ObservableObject
  {
      public TuningViewModel(ISmuReader smu, ITuningStore tuning,
                             IProcessWatcher watcher, ILogService log) { /* ... */ }
  }
  ```
- 前后端通信用 `WeakReferenceMessenger`（CommunityToolkit.Mvvm），不引事件总线、不引全局静态。
- 注册：新 ViewModel 若需容器托管，在 `AppHost.Build()` 里追加 `services.AddSingleton<T>()`（遵循「具体类型 + 接口映射」双注册原则）。

### 4.3 业务流转红线（来自项目规范，S5 落地时必守）
- **同步**：`SyncAllHardwareStateAsync` 同步 SMU（CO/FMax）与 Windows 电源策略，回填 UI。
- **下发**：`ApplyAllSettingsAsync` 下发前**必须读快照，失败必须回滚**；UI 锁死（Loading 遮罩）防假死。
- **防砖**：CO 限制 -50~+50；写入后**必须回读校验**，不一致立即还原。
- **场景防误切**：优先级 + 引用计数 + 状态栈。多规则命中按优先级覆盖；退出按栈回退，全部退出才恢复默认。
- **日志**：UI 内存上限 200 条（单条 ≤2000 字符），超出淘汰；按日异步落盘，**绝不阻塞 UI**。
- **生命周期**：托盘 `Shell_NotifyIcon` 自绘 + 监听 `WM_TASKBARCREATED`；单实例 `AppInstance`（非打包无 Package Identity → 改命名单锁）；`--silent` 崩溃静默退出，**严禁弹窗**。

### 4.4 全局 UI 规范（WinUI 3 版）
- **材质/标题栏**：默认 Mica + `ExtendsContentIntoTitleBar`，保留原生 Snap Layouts / 双击最大化。
- **主题自适应**：深 / 浅 / 跟随系统，**全部用 `ThemeResource`，严禁写死颜色**。
- **语义色铁律（不随强调色变，深浅两套字典）**：
  | 语义 | 深 | 浅 |
  |---|---|---|
  | Info | `#A3B8CC` | `#4A6076` |
  | Warn | `#F5D90A` | `#9D7B00` |
  | Error | `#FF453A` | `#C42B1C` |
- **自定义主题色**：强调色可由用户自定义（HSL/HEX），语义色恒定不变。
- **禁用系统白底控件**：下拉框 / 输入框 / 滚动条 / ToolTip 必须自定义模板覆盖深色；**全局下拉框须自定义**（深浅自适应、带缩放动画）。列表用 `ItemsRepeater` + 虚拟化，**严禁 `DataGrid`**。
- **控件/交互**：数值用 `NumberBox`；提示用 `InfoBar`；动画用 Composition API / Fluent 曲线；字号阶梯 L1~L6、圆角/高度/间距沿用规范，不发明新值。
- **编译底线**：0 警告 0 错误；XAML 全 `x:Bind`；JSON 源生成；`[LibraryImport]` 封送（UI 层本身不做 AOT，但不得破坏 Core 已验证的约束）。

---

## 5. 遗留技术债与避坑警告

> 这一节是给 S5（以及删 `Svc` 的 S6）接手者的「排雷清单」。每条都对应仓库里真实存在的代码或已证实的事故，不是泛泛而谈。

### 5.1 未落定 / 临时妥协代码（S3/S4 遗留，S5 处理时须知）

**有意保留、但新 AI 极易误判为「待清理」的妥协：**

1. **WPF 遗留层 35 处 `[DllImport]`**（`NativeTrayIcon.cs` 20 / `WakeWindow.cs` 10 / `WindowHelper.cs` 5）。这些是托盘窗口、唤醒消息、窗口过程，随 WPF 外壳在 S6 下线，**不要为它们做 AOT 迁移**——给即将删除的代码做精装修是纯损。
2. **`ProcessWatcher` 仍依赖 `System.Management`**（WMI 事件订阅，`Zentwik.Core/Services/ProcessWatcher.cs`）。这是 Core 中**唯一**未 AOT 化的部分。AOT 探针的 26 项检查未覆盖其 WMI 路径，所以 Core 目前只是「多数路径 AOT 0 警告」，而非「全路径」。**S5 必须把它改成 3 秒轮询 + `HashSet` 比对**（项目红线已定：后台监听改用轮询降级 + 集合比对防抖），否则 Native AOT 全量发布时 `System.Management` 会被裁剪掉或链接失败。
3. **`StartupCrashReporter.BuildPath()` 硬编码 `"Zentwik"` 文件夹名**（不调 `IAppPaths`）。刻意为之——崩溃点常就在 `AppPaths` 三级目录迁移里，用它会二次抛异常。代价：改名 / 迁移目录时**此处必须同步改**，否则崩溃日志写到旧路径，现场丢失。
4. **WinUI `Program.RunHeadless` 目前只写一条日志就退出**（`Zentwik.App/Program.cs`）。真正的后台绑核循环尚未接线，S5 接 Core 的 headless 调度时再补。
5. **`Zentwik.App.csproj` 中 `PublishTrimmed=false` 且无 `PublishAot`**。WinUI 外壳层不做裁剪/AOT（XAML 的 XBF 与 CsWinRT 投影都运行期按名解析，裁剪会白屏）。AOT 是 Core 的事，外壳留到 S7 单独立项。

**后台正确性关键状态（重写监听逻辑时别丢）：**
- `ProcessWatcher` 内部的规则去抖（`RuleReloadWindowMs=1000`）、存活集合比对（`_lastAlive` 只在集合真变了才广播）、PID 缓存（`PidSet` 带锁，`TryAdd` 的「查+写」原子）。这些共同保证「进程集合变化」语义正确、不刷屏、PID 不复用漏绑。

**未实机验证项**（各交付文档反复提及，影响发布判断，不是代码债但必须记着）：GUI / `--silent` 两条路径在 Ryzen 真机上的 CO 读写回读、Mica 表现、Snap Layouts 观感——S5 接线后必须真机复验。

### 5.2 S5 绝对禁忌（动这些会让 AOT 失效或跨线程崩溃）

**AOT 失效类（改了 → CI `-warnaserror` 直接红 / Native AOT 发布崩）：**

1. **给 Core 任何类加反射**：`JsonSerializer` 的 `Type` 重载、`Activator.CreateInstance`、`Type.GetType`、`dynamic`、表达式树编译 → 触发 `IL2026` / `IL2075` / `IL3050`。
2. **新增配置文件类型却忘了在 `Zentwik.Core/Serialization/AppJsonContext.cs` 登记 `[JsonSerializable]`** → 该类型的读写在 AOT 下**静默回落默认值**（老用户配置「被重置」且无任何报错）。这是最隐蔽的一类，加 Store / 配置类型时必查。
3. **新增 P/Invoke 用 `[DllImport]` 而非 `[LibraryImport]`**；或封送写法退回 `StringBuilder` / `[MarshalAs(UnmanagedType.ByValTStr)]` / `MarshalUsing` 命名空间写错 → `SYSLIB1051` 等源生成错误。
4. **把 `AppJsonContext` 的命名策略改成 `CamelCase`** → 老 `ui.json` / `tuning.json` 键全变 camelCase，读不出、静默回落。
5. **让 `SmuReader` 不再是 Singleton、新增第二个 `Cpu` 实例、或对它调用 `Dispose`**（`Dispose` 已 no-op，但有人可能「优化」掉）→ `Opcode.Close()` 释放进程级静态缓存，**所有** `Cpu` 失声（SMU 显示可用但 CO 全 null / FMax 全 0）。这是历史 0.7.1 / 0.7.2「SMU 被打哑」的根因，注释在 `SmuReader.cs` 第 41~80、147、663~673 行。

**跨线程崩溃类（改了 → 界面崩，事件查看器只有 0xc000027b / STA 异常）：**

6. **在后台线程直接碰 UI 集合 / 控件**：`ProcessWatcher.ProcessSetChanged`、进程快照回调、`LogService` 内存日志增删都发生在**非 UI 线程**。新 ViewModel 必须经 `IUiDispatcher`（即 `DelegatingUiDispatcher` 挂载后的真实实现）`Post` 回 UI 线程再改 `ObservableCollection`。直接改 = STA 异常。`ProcessWatcher` 内部已用 `SampleOnSamplerThread` 规避采样线程的 STA 问题（见 `TakeSnapshotForScan`），重写时不要退化回在 MTA 线程触发 `Refreshed` 事件。
7. **改动 `LogService` 的线程模型**（它依赖注入进来的 `IUiDispatcher` 做跨线程封送）：若改成「捕获当前 `SynchronizationContext`」或「无条件写 UI 集合」，后台 `--silent` 路径（无 UI 线程）会崩。
8. **动 `IUiDispatcher` / `DelegatingUiDispatcher` 的契约**：Core 红线是「Core 不认识任何 UI 框架类型」。若让 Core 直接引用 `DispatcherQueue` / WPF `Dispatcher`，AOT 与零依赖两条红线同时破。

### 5.3 删除 `Zentwik.Compat.Svc` 桥时的注意事项（S5 / S6）

**删除前提**：`Svc`（`Services/AppServices.cs`）只在 WPF 遗留层被引用（约 900 处 `Svc.Log` / `Svc.Smu` …），新 WinUI 页面一律构造函数注入接口、**不引用 `Svc`**。删除 `AppServices.cs` 与 `Zentwik.Compat` 命名空间前，先 `grep -rn "Svc\." --include=*.cs Zentwik.csproj` 确认 0 命中（WPF 视图随 S6 整体删除后即满足）。

**必须迁移到新 ViewModel / App 的 WPF 层状态**（这些状态当前由 WPF 服务持有，不迁出会在 S6 删 WPF 时一起消失）：

1. **主题与强调色状态**（`Helpers/ThemeService.cs`）：`Mode` / `IsDark` / `AccentColor` / `FollowSystemAccent` + 系统主题/强调色 2 秒轮询跟随（`StartSystemMonitor` / `StopSystemMonitor`，挂在 `PageTimerManager.HostScope`，窗口隐藏时挂起）→ WinUI 改用后台任务 + `ActualThemeChanged`。**强调色白名单 + 语义色黑名单双校验机制（`AccentOwnedKeys` / `SemanticColorKeys` + `WriteAccent`）必须原样保留**，否则「强调色盖掉语义色（红不再等于出错）」必复发。
2. **页面定时器与后台零占用**（`Services/PageTimerManager.cs`）：把「调用方意愿（Start/Stop）」与「当前是否允许跑（当前页 + 窗口可见）」解耦，隐藏/最小化/托盘时挂起所有刷新定时器 → WinUI 用 `DispatcherQueue` 定时器 + 页面生命周期 + 窗口 `Activated` / `VisibilityChanged` 重建同语义，否则后台白烧 CPU。
3. **导航 / 路由状态**（WPF 的 `NavigationRouter`）：当前页 key、页面实例字典、前进后退 → WinUI `NavigationView` + `Frame` + 页面实例管理。
4. **Toast / 窗口显隐 / 托盘**（`ToastService` / `GuiHost` / `TrayIconService` / `NativeTrayIcon`）：Toast 通知 → WinUI `InfoBar`；窗口显隐 → `AppWindow`；托盘仍走 `Shell_NotifyIcon`（原生）并监听 `WM_TASKBARCREATED` 重注册。
5. **进程监听的 UI 可见性闸门**：`ProcessWatcher.SetHostVisible(bool)` 由 WPF 窗口显隐驱动（降级轮询在隐藏时挂起）→ WinUI 窗口 `VisibilityChanged` / `Activated` 里继续调 `SetHostVisible`，否则隐藏时每 3 秒全量扫描白跑。
6. **全局异常 / 错误提示**（`ErrorNotifyService`）：全局错误弹窗/Toast → WinUI 接 `App.UnhandledException` + `InfoBar` / Toast。
7. **`LogService` 的 UI 绑定源**：WPF 日志页绑定 `LogService` 的内存日志（200 上限 / 单条 ≤2000 字符）→ WinUI 日志页仍绑定**同一** `ILogService` 实例（Singleton，禁止新建），否则两份队列、计数与淘汰错乱。

**生命周期契约在 S6 前不变**：S5 重写 UI 期间 WPF 与 WinUI 共存（`Zentwik.csproj` 仍是默认启动项，直到 S6 切换）。`Svc` 在 S5 阶段**还可暂时存在**（仅供 WPF 遗留页），但新代码一律不碰；真正的删除在 WPF 下线时一并做，不要提前删导致 WPF 遗留页编译失败。
