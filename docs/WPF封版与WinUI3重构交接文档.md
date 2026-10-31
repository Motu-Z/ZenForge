# WPF 封版与 WinUI 3 重构交接文档

> **状态：草稿 v0.9（待确认）** ｜ 生成时间：2026-10-31 ｜ 对应代码版本：`v1.2.0`
> 工程路径：`/workspace/projects/ZenForge`（`ZenForge.slnx` 管两个工程）
>
> 本文档由代码实际结构反向整理，**没有一处是凭印象写的**：类名、职责、P/Invoke 目标库、落盘路径
> 都能在仓库里对上。文末 §9 列出 10 个我无法从代码判断、只能由你决定的问题；
> 你回复后我会把答案合并进来出 v1.0 定稿。

---

## 0. 文档大纲

| 章节 | 内容 | 谁会看 |
|---|---|---|
| §1 | 封版基线：版本、Tag、构建与验证口径 | 你 / 接手的人 |
| §2 | 架构总览：两个工程、依赖方向、红线怎么守 | 接手的人 |
| §3 | `ZenForge.Core` 类清单（纯业务逻辑，可直接搬） | WinUI 3 开发者 |
| §4 | WPF 层类清单 + **迁移时必须重写的部分** | WinUI 3 开发者 |
| §5 | 数据落盘与系统状态（文件 / 注册表 / 驱动） | 运维 / 排错 |
| §6 | WinUI 3 迁移注意事项：P/Invoke、NuGet、AOT、XAML 差异、托盘、单实例 | 架构决策 |
| §7 | 建议的迁移顺序（按风险从低到高） | 排期 |
| §8 | 已知风险与「不要照搬」的坑 | 所有人 |
| §9 | 待你确认的 10 个问题 | 你 |

---

## 1. 封版基线

| 项 | 值 |
|---|---|
| 版本 | `1.2.0`（`Version` / `FileVersion` / `AssemblyVersion` 三处同步，两个工程一致） |
| Tag | `v1.2.0`（WPF 线终点，不创建 GitHub Release） |
| 目标框架 | `net10.0-windows`（App 带 `UseWPF`；Core 不带） |
| 构建 | `dotnet build ZenForge.slnx -c Release -p:EnableWindowsTargeting=true` |
| 结果 | **0 警告 0 错误**（两个工程） |
| 发布形态 | 单文件、框架依赖（`SelfContained=false`）、Release 不带 PDB |

> 构建命令里的 `EnableWindowsTargeting=true` 是给 Linux CI 用的；Windows 上不需要。

---

## 2. 架构总览

```
ZenForge.slnx
├── ZenForge.Core/          ← 纯业务逻辑（无 WPF）
│    ├── Services/          SMU、电源策略、日志、进程监听、方案/场景
│    └── Helpers/           JSON 落盘、四份配置 Store、内存回收
└── ZenForge/               ← WPF 外壳（本版封版，迁移时整体重写）
     ├── App.xaml(.cs)      启动路径、单实例、提权、全局异常
     ├── MainWindow         导航壳、关窗去向、生命周期
     ├── Views/             7 个页面 + 2 个独立窗口
     ├── Controls/          3 个自绘控件
     ├── Themes/            配色、排版、控件模板
     └── Services/          只服务 WPF 的东西（托盘、Toast、定时器、唤醒）
```

**依赖方向只有一条**：`ZenForge` → `ZenForge.Core`。Core 不知道 App 存在（唯一例外是 `InternalsVisibleTo("ZenForge")`，为了让 `internal` 的工具类可用，不构成反向依赖）。

**红线怎么守**：Core 的 csproj 不写 `UseWPF` / `UseWindowsForms`。没有这两个属性，SDK 不会引入 `PresentationFramework` / `WindowsBase` / `System.Drawing`，代码里出现 `System.Windows.*` 会**编译失败**。这不是靠自觉，是靠编译器。

> 实测校验（已跑过）：`ZenForge.Core.dll` 的引用表里没有 `PresentationFramework` / `PresentationCore` / `WindowsBase` / `System.Windows.Forms` / `System.Drawing`。

---

## 3. `ZenForge.Core` 类清单（迁移时可直接复用）

### 3.1 SMU / 硬件

| 类 | 职责 | 备注 |
|---|---|---|
| `SmuReader` | SMU 只读探测与写入封装（CO 逐核负压 / FMax），写完立即回读校验 | 依赖 `LLT.ZenStates-Core` + PawnIO 驱动 + 管理员权限；**单实例互斥**在类内 |
| `SmuDiagnostics` | 只读 + 回滚式自检，产出 txt 报告 | 供「通用页 → SMU 自检」调用 |
| `TuningApplyService` | 把 `tuning.json` 里的配置下发到 SMU，返回 `TuningApplyOutcome` | 启动时后台套用也走它 |
| `CpuSetBinder` | 基于 CPU Sets 的进程绑核（`SetProcessDefaultCpuSets`） | 只申请 `PROCESS_SET_LIMITED_INFORMATION`；`CpuSetBindResult.ErrorCode` 带出 Win32 码 |
| `ProcessPriority` | 进程优先级下发 + 档位映射 / 中文显示名 | `DisplayOf()` 是显示层唯一入口 |

### 3.2 电源与系统策略

| 类 | 职责 |
|---|---|
| `ProcessorPowerPolicy` | Windows 电源计划里的四个索引（电源策略档位 / 升频模式 / 核心停放 / EPP），**不是** SMU 层 PBO |
| `ProcessorPowerTuning` | 升频模式（`PERFBOOSTMODE`）读写与档位表 `BoostOptions` |
| `PowerStatusWatcher` | 供电状态监听：`SystemEvents.PowerModeChanged` 为主 + 30 秒轮询兜底（P/Invoke `GetSystemPowerStatus`） |
| `AutoStartService` | 开机自启（注册表 Run 项） |

### 3.3 日志

| 类 | 职责 |
|---|---|
| `LogService` | 内存环形日志（上限 320 条 / 单条 2000 字符）+ 批量落盘队列（上限 800）+ `EntryAdded` 事件 |
| `LogThrottle` | 同 key 60 秒节流，补报「另有 N 条已抑制」 |

> `LogService.UiContext` 是 `SynchronizationContext`，**不是** WPF 类型 —— 迁 WinUI 3 时把 `DispatcherQueue.AsSynchronizationContext()` 传进来即可，代码不用改。

### 3.4 进程监听

| 类 | 职责 |
|---|---|
| `ProcessRefresher` | 进程采样（Toolhelp32 快照），窗口不可见时整条循环停摆 |
| `ProcessWatcher` | 规则匹配 + 绑核 + 优先级下发；WMI 事件为主（`__InstanceCreationEvent` / `__InstanceDeletionEvent`），不可用时降级 3 秒轮询；`ReapplyNow()` 对现存进程重扫 |
| `ProcessPriority` | 见 §3.1 |
| `UiPresence` | 「主窗口是否可见」的全局标志，三处闸门共用 |

### 3.5 方案与场景

| 类 | 职责 |
|---|---|
| `ProfileService` | 多方案保存/切换（快照 = CO + FMax + 绑核规则 + 优先级），切换时写回三个 Store 并调 `ReapplyNow()` |
| `ScenarioService` | 场景规则仲裁（进程运行 / 电池 / 交流），按 `Priority` 取最小；`Notify()` 收「此刻全部成立的触发」 |
| `ScenarioMonitor` | 编排层：按规则内容决定监听谁、800ms 防抖、调用侧封送到 UI 线程 |
| `ScenarioSelfCheck` | `-selftest` 自检（仲裁 / 监听范围 / 快照深拷贝 / 生效标记），不下发硬件 |

### 3.6 Store 与基础设施

| 类 | 职责 | 落盘 |
|---|---|---|
| `UiSettingsStore` | 主题、强调色、托盘、后台服务开关等 | `%AppData%\ZenForge\ui.json` |
| `TuningStore` | CO 逐核 + FMax | `tuning.json` |
| `BindingStore` | 绑核规则（进程名 → CCD / 掩码 / 优先级） | `bindings.json` |
| `MaskStore` | 自定义核心掩码 | `masks.json` |
| `ProfileService` | 方案 | `profiles.json` |
| `ScenarioService` | 场景规则 | `scenarios.json` |
| `JsonFile` | 统一的 JSON 读写（缩进、损坏兜底） | — |
| `MaskFormatter` / `MaskNamePolicy` | 掩码 ↔ 区间文本、掩码命名规则 | — |
| `MemoryTrimmer` | GC + LOH 压缩 + `SetProcessWorkingSetSize`（交还工作集） | — |

---

## 4. WPF 层：迁移时必须重写的部分

### 4.1 完全重写（WinUI 3 无对应物）

| 位置 | 内容 | 为什么必须重写 |
|---|---|---|
| `MainWindow.xaml(.cs)` | 自绘无边框窗口、侧栏导航、拖拽/最大化边界处理 | 窗口 chrome 机制不同（`AppWindow` / `ExtendsContentIntoTitleBar`） |
| `Views/*.xaml`（7 个页面） | 全部版式 | `DataTrigger` / `Style` / `ControlTemplate` 在 WinUI 3 里不是同一套东西 |
| `Views/DarkDialog`、`MaskManagerWindow` | 两个独立窗口 | `Window` 语义、Owner、模态行为都不同 |
| `Themes/*.xaml` | `Controls.xaml`（约 1500 行模板）、`Palette.*`、`Typography`、`SearchBox` | 控件模板体系不同，`DynamicResource` → `ThemeResource`，`Trigger` → `VisualStateManager` |
| `Controls/SearchBox` | 自绘搜索框 | 事件模型 / 模板部件名不同 |
| `Controls/SmoothSortPanel` | 平滑排序面板（`VirtualizingStackPanel` 子类） | WinUI 3 无 `VirtualizingStackPanel` 可继承，要换 `ItemsRepeater` + `Layout` 自定义 |
| `Controls/ToastHost` | 轻提示层 | 需改用 `TeachingTip` / `InfoBar` 或自绘 |
| `Helpers/ThemeService`、`ColorHsl`、`WindowHelper`、`UiTextOptions` | 主题换肤、DWM 属性、文本渲染模式 | 全部依赖 WPF 资源/视觉树/`TextOptions` |

> `UiTextOptions` 在 WinUI 3 里**没有对应物**：WinUI 用 DirectWrite，不存在 `TextFormattingMode` / `TextHintingMode`。这一层整体删除，不要试图移植。

### 4.2 保留但需替换实现（逻辑可复用）

| 位置 | WPF 实现 | WinUI 3 要换成 |
|---|---|---|
| `Services/TrayIconService` | `System.Windows.Forms.NotifyIcon` + WPF `ContextMenu` | Win32 `Shell_NotifyIcon` + 自绘菜单（见 §6.4） |
| `Services/NativeTrayIcon` | 原生托盘图标封装 | 同上，可直接复用 P/Invoke 部分 |
| `Services/PageTimerManager` | `DispatcherTimer` | `DispatcherQueueTimer` |
| `Services/ToastService` | 静态宿主引用 | 保留门面，宿主换成新控件 |
| `Services/SingleInstance` | `Mutex` + 命名事件 + `FindWindow` | `AppInstance.FindOrRegisterForKey` + 重定向（见 §6.5） |
| `Services/WakeWindow` | 隐藏窗口 + `PostMessage` 唤醒 | 同上，由 `AppInstance` 接管 |
| `Services/ErrorNotifyService` | 按「窗口可见」选 Toast / 托盘气泡 | 保留判定，通道换掉 |
| `Services/FactoryResetService` | 清配置 + 解绑 + 卸载驱动 | 逻辑基本可复用，只换提示通道 |
| `Services/HeadlessHost` / `SilentModeRunner` | `-silent` 无界面常驻 | 见 §9 问题 8 |
| `App.xaml.cs` | 启动路径、提权、全局异常、`ExitApplication` | 需按 WinUI 3 的 `App` + `AppWindow` 重写 |

---

## 5. 数据落盘与系统状态

| 类型 | 位置 | 说明 |
|---|---|---|
| 配置 | `%AppData%\ZenForge\*.json` | 6 个文件，见 §3.6；迁移时**必须沿用同一路径**，否则用户配置全丢 |
| 日志 | 同一目录下的日期分文件 | 由 `LogService` 维护，含 Debug 级完整文本 |
| 自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | `AutoStartService` |
| 电源方案 | 通过 `powrprof.dll` 读写当前方案的隐藏索引 | 不新建电源方案，只改索引值 |
| SMU | PawnIO 驱动（随 ZenStates-Core 安装） | 需管理员权限；写入前的防砖保护在「高级」页 |
| 单实例 | 全局 `Mutex` + 命名事件 | 迁移后由 `AppInstance` 取代 |

---

## 6. WinUI 3 迁移注意事项

### 6.1 P/Invoke

当前所有原生调用（迁移后**一处都不用改签名**，只建议换写法）：

| 库 | 次数 | 用途 |
|---|---|---|
| `kernel32.dll` | 22 | `OpenProcess` / `CloseHandle` / `SetProcessDefaultCpuSets` / `GetSystemCpuSetInformation` / `SetProcessWorkingSetSize` / Toolhelp 快照 |
| `user32.dll` | 31 | 窗口样式、`FindWindow`、`PostMessage`、`SetWindowPos`、前台激活 |
| `powrprof.dll` | 11 | `PowerReadACValueIndex` / `PowerWriteACValueIndex` 等电源索引读写 |
| `dwmapi.dll` | 1 | 深色标题栏（`DWMWA_USE_IMMERSIVE_DARK_MODE`） |
| `shell32.dll` | 1 | 打开所在目录 |

AOT 相关：
- `[DllImport]` 在 AOT 下可用，但**推荐换 `[LibraryImport]`**（源生成器，避免运行时封送）。
- 换 `LibraryImport` 时注意三处会编译报错的地方：`bool` 返回值要写 `[return: MarshalAs(UnmanagedType.Bool)]`；`uint[]` 参数（`SetProcessDefaultCpuSets`）需要显式 `[MarshalUsing]` 或改 `unsafe` 指针；`Pack=4` 的结构体要保留 `StructLayout`。
- 如果为了省事开了 `<DisableRuntimeMarshalling>`，上面这些 `bool` / 数组封送会**在运行期**炸，不是编译期。

### 6.2 第三方 NuGet 与 AOT

| 包 | 现状 | AOT 风险 |
|---|---|---|
| `LLT.ZenStates-Core` 1.92.4 | 只有 `net9.0` 资产，被 `net10.0` 引用 | **必须实测**。它封装了 PawnIO 驱动通信，如果内部用了反射/动态加载，AOT 下会被裁掉。先跑 `dotnet publish -p:PublishAot=true` 看 `IL2xxx` 警告 |
| `System.Management` 9.0.0 | WMI 事件订阅（进程创建/退出） | **高风险**。它建立在 COM 互操作 + `ManagementObject` 反射之上，AOT 下大概率不可用。见 §9 问题 4 |
| `Microsoft.Win32.SystemEvents` 9.0.0 | 电源模式通知 | 中等。它需要一个消息泵与隐藏窗口，AOT 下可用但行为依赖线程模型；已有 30 秒轮询兜底，实在不行砍掉事件通道 |

> 判断标准不要用「编译过没过」，要用 `dotnet publish -c Release -p:PublishAot=true -p:EnableTrimAnalyzer=true` 的 **IL2xxx / IL3xxx 警告清单**。

### 6.3 XAML 与线程模型

| WPF | WinUI 3 | 影响 |
|---|---|---|
| `{Binding}`（反射） | `{x:Bind}`（编译期生成） | AOT 下**必须**全量 `x:Bind`，否则绑定会在裁剪后失效 |
| `DataTrigger` / `Trigger` | `VisualStateManager` + `StateTrigger` | 进程行「选中/悬停/闪青」三处状态要重写 |
| `DynamicResource` | `ThemeResource` | 换肤逻辑要重做（资源查找时机不同） |
| `SynchronizationContext`（`LogService.UiContext`） | `DispatcherQueue.AsSynchronizationContext()` | **Core 不用改**，注入点换一行 |
| `ObservableCollection` + `ItemsControl` | `ItemsRepeater` / `ListView` | 几百行进程列表要用 `ItemsRepeater` + 自定义 `Layout` 才有虚拟化 |
| `TextOptions.TextFormattingMode` | 无 | 整层删除 |

### 6.4 托盘图标

WinUI 3 **没有** `NotifyIcon`。三条路：

1. **自己 P/Invoke `Shell_NotifyIcon`**（推荐）：只需要图标 + 4 个菜单项（打开界面 / 暂停绑核 / 退出），量小、零依赖、AOT 安全，代价是要自己处理 `WM_TASKBARCREATED`（资源管理器重启）。
2. `H.NotifyIcon.WinUI`：省事，但要确认它的 AOT/裁剪兼容性，且多一个第三方依赖。
3. 不做托盘：与「后台常驻」定位冲突，不建议。

### 6.5 单实例与唤醒

WPF 版是 `Mutex` + 命名事件 + `FindWindow` 三段式（`SingleInstance` + `WakeWindow`）。WinUI 3 应换 `Microsoft.Windows.AppLifecycle.AppInstance`：

- `FindOrRegisterForKey("ZenForge")` 判重；
- 重复实例用 `RedirectActivationToAsync` 把激活转给主实例，**不用**再找窗口句柄；
- 好处：多实例、协议激活（`zenforge://`）以后都好加；代价：双击 exe 的唤醒路径变了，`-silent` 那条路要重新设计（见 §9 问题 6/8）。

### 6.6 发布形态

- WPF 用不了 Native AOT（`UseWPF` 与 `PublishAot` 互斥）。这正是迁 WinUI 3 的核心理由，但也意味着 **AOT 是否真的可行要在 WinUI 3 上从零验证**。
- 建议先做「非 AOT 的 WinUI 3 可运行版」，功能对齐后再开 `PublishAot` —— 一次性两个变量（UI 框架 + AOT）同时动，出问题分不清是谁的。
- 未打包（unpackaged）+ 自包含 是最省事的组合：`WindowsAppSDKSelfContained=true` + `SelfContained=true` + `PublishSingleFile`。打包（MSIX）会带来签名与更新链路，本阶段不必。

---

## 7. 建议的迁移顺序

| 步 | 内容 | 验收 |
|---|---|---|
| 1 | 新建 WinUI 3 空壳，引用 `ZenForge.Core`，能读到 `ui.json` 并显示 CPU 名 | Core 原封不动能跑 |
| 2 | 调校页（只读）：SMU 快照 → 界面 | 数字与 WPF 版一致 |
| 3 | 调校页（写）：CO / FMax 下发 + 回读校验 | 写入后回读一致 |
| 4 | 进程页：`ProcessRefresher` + `ItemsRepeater` | 300 进程下列表可滚动、不卡 |
| 5 | 绑核监听：`ProcessWatcher` + 规则表 | 启动游戏自动绑核 |
| 6 | 日志页 + 设置页 + 托盘 | 关窗后进程常驻、内存与 WPF 版对比 |
| 7 | `PublishAot=true` 逐项排雷 | IL 警告清零，各功能回归 |

---

## 8. 已知风险与「不要照搬」的坑

1. **不要照搬 `SmoothSortPanel`**。它的做法是「布局值用新的、渲染位置从旧值补间」，依赖 WPF 的 `RenderTransform` 与 `VirtualizingStackPanel` 的 `Arrange` 时机。WinUI 3 的 `ItemsRepeater` 布局模型完全不同，硬搬会得到「行乱飞」。
2. **不要照搬 `Themes/Controls.xaml`**。1500 行模板里大量依赖 WPF 的 `Trigger` / `TemplateBinding` / `PART_*` 约定，逐条翻译的成本高于重写。
3. **`Application.MainWindow` 那类静态强引用陷阱在 WinUI 3 里同样存在**（`App.MainWindow` / 静态 `Window` 字段）。关窗即销毁这条纪律要带过去。
4. **WMI 是最大的不确定项**。如果 AOT 下 `System.Management` 不可用，替代方案是「降级轮询」（代码里已经有这条路径，3 秒一轮），代价是新进程绑核延迟最多 3 秒。
5. **`-silent` 无界面模式**是 v1.1 场景自动切换的主战场。WinUI 3 下若不能在没有 XAML 的情况下跑（`App` 必须初始化 XAML 框架），要把「后台调度」拆成独立进程或独立入口 —— 这是架构级决定，见 §9 问题 8。

---

## 9. 待确认的 10 个问题

> 每条都给了「我现在的假设」和「你若改成另一种，我改哪里」。你只需要回「几号：选择」，或直接说结论。

| # | 问题 | 我现在的假设 | 若改变的影响 |
|---|---|---|---|
| 1 | `ZenForge.Core` 的命名空间要不要一并改成 `ZenForge.Core.*`？ | **不改**，保留 `ZenForge.Services` / `ZenForge.Helpers` | 改的话是全局 `using` 重写，纯体力活，但能让程序集边界在代码里一眼可见 |
| 2 | WPF 封版后，Core 是否允许继续加功能（还是完全冻结，只修 bug）？ | **允许加**，但不动 WPF 层 | 若冻结，`v1.1.x` 待办（场景规则编辑器等）只能等 WinUI 3 |
| 3 | SMU 通道是否仍用 `LLT.ZenStates-Core`（PawnIO 驱动）？ | **继续用**，不换自研封装 | 自研要重做驱动通信与防砖保护，工作量以周计 |
| 4 | Native AOT 下若 `System.Management` 不可用，接受「砍 WMI 事件、改 3 秒轮询」吗？ | **接受**（已有降级路径） | 不接受就得换 ETW（需管理员）或 MI .NET，两者都要重写监听层 |
| 5 | 托盘图标必须保留吗？ | **保留**，自写 `Shell_NotifyIcon` | 不保留则「后台常驻」没有入口，只能靠双击 exe |
| 6 | 单实例/唤醒改用 Windows App SDK 的 `AppInstance`？ | **改**，并顺手支持 `zenforge://` 激活 | 不改就得在 WinUI 3 里继续 `FindWindow`，脆弱且要多一个隐藏窗口 |
| 7 | 强调色是否继续「跟随系统强调色」并自绘调色板？ | **继续跟随**（`ColorHsl` 的算法搬过去） | 若改成 WinUI 内置主题色，`ColorHsl` 可删，但界面会失去现在的配色一致性 |
| 8 | `-silent`（无界面常驻）是否必须保留？ | **保留**，做成独立入口/独立进程 | 这是最大的一条：若不保留，场景自动切换就必须依赖界面进程活着 |
| 9 | 平滑排序动画参数（200ms 缓动、单屏位移上限）沿用吗？ | **沿用**，做成可调常量 | 换实现时参数要重新调，建议先固定再用真机微调 |
| 10 | 界面术语表是否按本版口径固定（`核心电压偏移` / `最高加速频率` / `电源策略控制` / `SMU 已联通`；`CCD`、`FMax`、`CO` 保留英文）？ | **按此固定** | 若要统一术语（例如 `FMax` 也译成中文），我改 UI 文案与日志 |

---

## 附录 A：本版（v1.2.0）改动清单

详见 `CHANGELOG.md` 的 `[1.2.0]` 段。摘要：

1. 关窗即销毁（废弃秒开），补齐三处静态强引用与定时器解绑；
2. `OpenProcess` 改最小必要权限 + 同类失败 60 秒日志节流；
3. 调校页三卡标题中文化并统一 L2；SMU 状态改为「已联通」；
4. 搜索框：占位符绑定缺失、光标垂直偏移、聚焦即消失；
5. 优先级档位汉化（数据显示分离：`binding.json` 仍是英文常量）；
6. 抽离 `ZenForge.Core`（零 WPF 引用，编译器守红线）。

## 附录 B：一条命令验证红线

```bash
# 1) Core 单独编译（出现任何 System.Windows 类型就会在这里失败）
dotnet build ZenForge.Core/ZenForge.Core.csproj -c Release

# 2) 产物引用表里不应出现 WPF 程序集
strings ZenForge.Core/bin/Release/net10.0-windows/ZenForge.Core.dll \
  | grep -E "^(PresentationFramework|PresentationCore|WindowsBase|System.Windows.Forms|System.Drawing)$"
# 无输出 = 通过
```
