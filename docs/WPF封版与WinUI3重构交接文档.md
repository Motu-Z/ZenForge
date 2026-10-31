# Zenwik：WPF 封版与 WinUI 3 重构交接文档

> **状态：v1.0.1 定稿** ｜ 更新：2026-10-31 ｜ 对应代码版本：`v1.2.1`（Tag `v1.2.1`）
> 解决方案：`Zenwik.slnx`（管 `Zenwik` 与 `Zenwik.Core` 两个工程）
>
> v0.9 里悬着的 10 个问题已全部拍板，§9 改成**决策记录**（结论 + 实施落点 + 影响面）。
> 本文档由代码实际结构反向整理：类名、职责、P/Invoke 目标库、落盘路径都能在仓库里对上。

---

## 0. 文档大纲

| 章节 | 内容 | 谁会看 |
|---|---|---|
| §1 | 封版基线：版本、Tag、构建与验证口径 | 你 / 接手的人 |
| §2 | 架构总览：两个工程、依赖方向、红线怎么守 | 接手的人 |
| §3 | `Zenwik.Core` 类清单（纯业务逻辑，可直接搬） | WinUI 3 开发者 |
| §4 | WPF 层类清单 + **迁移时必须重写的部分** | WinUI 3 开发者 |
| §5 | 数据落盘与系统状态（文件 / 注册表 / 驱动） | 运维 / 排错 |
| §6 | WinUI 3 迁移注意事项：P/Invoke、依赖、AOT、XAML 差异、托盘、单实例 | 架构决策 |
| §7 | 建议的迁移顺序（按风险从低到高） | 排期 |
| §8 | 已知风险与「不要照搬」的坑 | 所有人 |
| §9 | 决策记录（10 条已拍板事项） | 你 / 接手的人 |
| 附录 A | v1.2.1 改动清单 | 查变更 |
| 附录 B | 一条命令验证红线 | CI |

---

## 1. 封版基线

| 项 | 值 |
|---|---|
| 版本 | `1.2.1`（`Version` / `FileVersion` / `AssemblyVersion` 三处同步，两个工程一致） |
| Tag | `v1.2.1` |
| 目标框架 | `net10.0-windows`（`Zenwik` 带 `UseWPF`；`Zenwik.Core` 不带） |
| 构建 | `dotnet build Zenwik.slnx -c Release -p:EnableWindowsTargeting=true` |
| 结果 | **0 警告 0 错误**（两个工程） |
| 发布 | `dotnet publish Zenwik.csproj -c Release -r win-x64` → `Zenwik.exe` + `WinIo32.dll` |
| 发布形态 | 单文件、框架依赖（`SelfContained=false`）、Release 不带 PDB |

> 构建命令里的 `EnableWindowsTargeting=true` 是给 Linux CI 用的；Windows 上不需要。
>
> 本地检出目录名可能仍叫 `ZenForge`，与工程名无关 —— 别被路径误导，认 `Zenwik.slnx`。

---

## 2. 架构总览

```
Zenwik.slnx
├── Zenwik.Core/            ← 纯业务逻辑（无 WPF），命名空间 Zenwik.Core.*
│    ├── Services/          SMU、电源策略、日志、进程监听、方案/场景
│    └── Helpers/           JSON 落盘、五份配置 Store、路径出口、内存回收
└── Zenwik/                 ← WPF 外壳（本版封版，迁移时整体重写）
     ├── App.xaml(.cs)      启动路径、单实例、提权、全局异常
     ├── MainWindow         导航壳、关窗去向、生命周期
     ├── Views/             7 个页面 + 2 个独立窗口
     ├── Controls/          3 个自绘控件
     ├── Themes/            配色、排版、控件模板
     └── Services/          只服务 WPF 的东西（托盘、Toast、定时器、唤醒）
```

**依赖方向只有一条**：`Zenwik` → `Zenwik.Core`。Core 不知道 App 存在（唯一例外是
`InternalsVisibleTo("Zenwik")`，为了让 `internal` 工具类可用，不构成反向依赖）。

**命名空间边界**：v1.2.1 起 Core 下沉一层（`Zenwik.Core.Services` / `Zenwik.Core.Helpers`），
与 WPF 层的 `Zenwik.Services` / `Zenwik.Helpers` 不再混居。WPF 层对 Core 的依赖在
`Zenwik.csproj` 里声明为两条全局 `using` —— 这是一条**全局无条件**的依赖（每个页面都在写日志），
不是按文件斟酌的可选项。

**红线怎么守**：Core 的 csproj 不写 `UseWPF` / `UseWindowsForms`。没有这两个属性，SDK 不会引入
`PresentationFramework` / `WindowsBase` / `System.Drawing`，代码里出现 `System.Windows.*` 会**编译失败**。
这不是靠自觉，是靠编译器。

> 实测校验（已跑过）：`Zenwik.Core.dll` 的引用表里没有 `PresentationFramework` / `PresentationCore` /
> `WindowsBase` / `System.Windows.Forms` / `System.Drawing`。

---

## 3. `Zenwik.Core` 类清单（迁移时可直接复用）

### 3.1 SMU / 硬件

| 类 | 职责 | 备注 |
|---|---|---|
| `SmuReader` | SMU 只读探测与写入封装（CO 逐核负压 / FMax），写完立即回读校验 | 依赖 `ZenStates-Core` 1.93.1（本地 DLL）+ PawnIO 驱动 + 管理员权限；**单实例互斥**在类内 |
| `SmuDiagnostics` | 只读 + 回滚式自检，产出 txt 报告 | 供「通用页 → SMU 自检」调用 |
| `TuningApplyService` | 把 `tuning.json` 里的配置下发到 SMU，返回 `TuningApplyOutcome` | 启动时后台套用也走它 |
| `CpuSetBinder` | 基于 CPU Sets 的进程绑核（`SetProcessDefaultCpuSets`） | 只申请 `PROCESS_SET_LIMITED_INFORMATION`；`CpuSetBindResult.ErrorCode` 带出 Win32 码 |
| `ProcessPriority` | 进程优先级下发 + 档位映射 / 中文显示名 | `DisplayOf()` 是显示层唯一入口 |

**SMU 通道的关键约束**（1.93.1 起，详见 §6.2 与 `Zenwik.Core.csproj` 的注释）：

- `Cpu` **没有无参构造**，必须传 `CoreOptions`；本实现传 `CoreOptions.Current`（库内的进程级单例配置）。
- `SendTestMessage(uint arg = 0, Mailbox mbox = null)` 两个参数都可选，`SendTestMessage()` 老写法仍有效。
- 拓扑（`info.topology.*`）、消息号（`smu.Rsmu.SMU_MSG_*`）、`SetPsmMarginSingleCore` /
  `GetPsmMarginSingleCore` / `MakeCoreMask` / `GetFMax` / `SetFMax` **签名未变**。
- `Cpu.Dispose()` 依然不能调：它内部会 `Opcode.Close()`，而 `Opcode` 是进程级静态资源，
  销毁任一个实例会让**所有** `Cpu` 的 CPUID / SMU 能力失效。`SmuReader.Dispose()` 保持空实现。

### 3.2 电源与系统策略

| 类 | 职责 |
|---|---|
| `ProcessorPowerPolicy` | Windows 电源计划里的四个索引（电源策略档位 / 升频模式 / 核心停放 / EPP），**不是** SMU 层 PBO |
| `ProcessorPowerTuning` | 升频模式（`PERFBOOSTMODE`）读写与档位表 `BoostOptions` |
| `PowerStatusWatcher` | 供电状态监听：`SystemEvents.PowerModeChanged` 为主 + 30 秒轮询兜底（P/Invoke `GetSystemPowerStatus`） |
| `AutoStartService` | 开机自启（计划任务 `ZenwikAutoStart` + `-silent`） |

### 3.3 日志

| 类 | 职责 |
|---|---|
| `LogService` | 内存环形日志（上限 320 条 / 单条 2000 字符）+ 批量落盘队列（上限 800）+ `EntryAdded` 事件 |
| `LogThrottle` | 同 key 60 秒节流，补报「另有 N 条已抑制」 |

> `LogService.UiContext` 是 `SynchronizationContext`，**不是** WPF 类型 —— 迁 WinUI 3 时把
> `DispatcherQueue.AsSynchronizationContext()` 传进来即可，代码不用改。

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
| `AppPaths` | **产品路径唯一出口**：根目录、`Logs`、`File(name)`、`Dir(name)`，并负责旧目录一次性迁移 | — |
| `UiSettingsStore` | 主题、强调色、托盘、后台服务开关等 | `%AppData%\Zenwik\ui.json` |
| `TuningStore` | CO 逐核 + FMax | `tuning.json` |
| `BindingStore` | 绑核规则（进程名 → CCD / 掩码 / 优先级） | `bindings.json` |
| `MaskStore` | 自定义核心掩码 | `masks.json` |
| `ProfileService` | 方案 | `profiles.json` |
| `ScenarioService` | 场景规则 | `scenarios.json` |
| `JsonFile` | 统一的 JSON 读写（缩进、损坏兜底） | — |
| `MaskFormatter` / `MaskNamePolicy` | 掩码 ↔ 区间文本、掩码命名规则 | — |
| `MemoryTrimmer` | GC + LOH 压缩 + `SetProcessWorkingSetSize`（交还工作集） | — |

> 所有落盘路径都从 `AppPaths` 取，**没有第二处硬编码目录名**。改名前那个字面量在 9 个文件里各写了一遍，
> 漏改一处就会得到「设置能存、方案读不出来」这种不报错的错乱状态。

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

> `UiTextOptions` 在 WinUI 3 里**没有对应物**：WinUI 用 DirectWrite，不存在 `TextFormattingMode` /
> `TextHintingMode`。这一层整体删除，不要试图移植。

### 4.2 保留但需替换实现（逻辑可复用）

| 位置 | WPF 实现 | WinUI 3 要换成 |
|---|---|---|
| `Services/TrayIconService` | `System.Windows.Forms.NotifyIcon` + WPF `ContextMenu` | Win32 `Shell_NotifyIcon` + 自绘菜单（见 §6.4）；`NativeTrayIcon` 的 P/Invoke 可直接复用 |
| `Services/PageTimerManager` | `DispatcherTimer` | `DispatcherQueueTimer` |
| `Services/ToastService` | 静态宿主引用 | 保留门面，宿主换成新控件 |
| `Services/SingleInstance` | `Mutex` + 命名事件 + `FindWindow` | `AppInstance.FindOrRegisterForKey` + 重定向（见 §6.5） |
| `Services/WakeWindow` | 隐藏窗口 + `PostMessage` 唤醒 | 同上，由 `AppInstance` 接管 |
| `Services/ErrorNotifyService` | 按「窗口可见」选 Toast / 托盘气泡 | 保留判定，通道换掉 |
| `Services/FactoryResetService` | 清配置 + 解绑 + 卸载驱动 | 逻辑基本可复用，只换提示通道 |
| `Services/HeadlessHost` / `SilentModeRunner` | `-silent` 无界面常驻 | 见 §9 决策 8 |
| `App.xaml.cs` | 启动路径、提权、全局异常、`ExitApplication` | 需按 WinUI 3 的 `App` + `AppWindow` 重写 |

---

## 5. 数据落盘与系统状态

| 类型 | 位置 | 说明 |
|---|---|---|
| 配置 | `%AppData%\Zenwik\*.json` | 6 个文件，见 §3.6；迁移 WinUI 3 时**必须沿用同一路径**，否则用户配置全丢 |
| 旧配置 | `%AppData%\ZenForge\` | 仅在升级后首次启动时被搬走（`Move`，失败退化为复制）；搬完后该目录不再存在 |
| 日志 | `%AppData%\Zenwik\Logs\` | 日期分文件，由 `LogService` 维护，含 Debug 级完整文本 |
| SMU 诊断 | `%AppData%\Zenwik\Diagnostics\` | `SmuDiagnostics` 产出的 txt 报告 |
| 自启 | 计划任务 `ZenwikAutoStart` | `AutoStartService`；旧的 `ZenForgeAutoStart` 不会自动删除 |
| 电源方案 | 通过 `powrprof.dll` 读写当前方案的隐藏索引 | 不新建电源方案，只改索引值 |
| SMU | PawnIO 驱动（管理员权限） | 驱动侧模块由 `ZenStates-Core` 内置资源加载 |
| 单实例 | `Local\Zenwik.SingleInstance` + 命名事件 | 迁移后由 `AppInstance` 取代 |
| 绑核互斥 | `Global\Zenwik.ProcessWatcher.Singleton` | 防止两处同时写绑核 |

---

## 6. WinUI 3 迁移注意事项

### 6.1 P/Invoke

当前所有原生调用（迁移后**一处都不用改签名**，只建议换写法）：

| 库 | 次数 | 用途 |
|---|---|---|
| `kernel32.dll` | 22 | `OpenProcess` / `CloseHandle` / `SetProcessDefaultCpuSets` / `GetSystemCpuSetInformation` / `SetProcessWorkingSetSize` / Toolhelp 快照 |
| `user32.dll` | 31 | 窗口样式、`FindWindow`、`PostMessage`、`SetWindowPos`、前台激活、`Shell_NotifyIcon` |
| `powrprof.dll` | 11 | `PowerReadACValueIndex` / `PowerWriteACValueIndex` 等电源索引读写 |
| `dwmapi.dll` | 1 | 深色标题栏（`DWMWA_USE_IMMERSIVE_DARK_MODE`） |
| `shell32.dll` | 1 | 打开所在目录 |

AOT 相关：
- `[DllImport]` 在 AOT 下可用，但**推荐换 `[LibraryImport]`**（源生成器，避免运行时封送）。
- 换 `LibraryImport` 时注意三处会编译报错的地方：`bool` 返回值要写
  `[return: MarshalAs(UnmanagedType.Bool)]`；`uint[]` 参数（`SetProcessDefaultCpuSets`）需要显式
  `[MarshalUsing]` 或改 `unsafe` 指针；`Pack=4` 的结构体要保留 `StructLayout`。
- 如果为了省事开了 `<DisableRuntimeMarshalling>`，上面这些 `bool` / 数组封送会**在运行期**炸，不是编译期。

### 6.2 第三方依赖与 AOT

| 依赖 | 版本与形态 | AOT 风险 |
|---|---|---|
| `ZenStates-Core` | **1.93.1，本地 DLL 引用**（`libs/ZenStates-Core/ZenStates-Core.dll`，取自包内 `net8.0-windows` 资产） | **必须实测**。它封装 PawnIO 驱动通信与 SMU 邮箱，内部有反射与 `Assembly` 资源加载（PawnIO 内核模块以嵌入资源 `ZenStates.Core.Resources.PawnIo.*.bin` 形式存在，靠 `LoadModuleFromResource` 加载）。AOT 裁剪会把这类资源与反射路径一起带走 |
| `System.Management` | 9.0.0（NuGet） | **高风险**。COM 互操作 + `ManagementObject` 反射，AOT 下大概率不可用；决策见 §9 决策 4 |
| `Microsoft.Win32.SystemEvents` | 9.0.0（NuGet） | 中等。需要消息泵与隐藏窗口，AOT 下可用但依赖线程模型；已有 30 秒轮询兜底 |
| `System.ServiceProcess.ServiceController` | 9.0.0（NuGet，v1.2.1 起显式声明） | 低。只用于 `SmuReader` 唤醒 PawnIO 服务 |

> **本地 DLL 引用带来的两个连带责任**（改成 `Reference` 后 NuGet 的传递依赖不再生效）：
> 1. `System.ServiceProcess.ServiceController` 必须自己声明，否则 `using System.ServiceProcess` 编译失败；
> 2. `WinIo32.dll` 必须随 exe 落地（见下）。
>
> **发布形态**：`WinIo32.dll` 用 `<ExcludeFromSingleFile>true</ExcludeFromSingleFile>` 排除在单文件之外，
> 因此产物是 `Zenwik.exe` + `WinIo32.dll` 两个文件。原因：`ZenStates-Core` 是按**文件名**自己
> `LoadLibrary("WinIo32.dll")` 的，它只认 exe 所在目录与 `PATH`；塞进单文件包会被解到
> `%TEMP%\.net\...`，运行期找不到。它是 PawnIO 缺位时回退端口访问的兜底。

> 判断标准不要用「编译过没过」，要用
> `dotnet publish -c Release -p:PublishAot=true -p:EnableTrimAnalyzer=true` 的 **IL2xxx / IL3xxx 警告清单**。

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

1. **自己 P/Invoke `Shell_NotifyIcon`**（已定方案，见 §9 决策 5）：只需图标 + 4 个菜单项
   （打开界面 / 暂停绑核 / 退出），量小、零依赖、AOT 安全；代价是要自己处理 `WM_TASKBARCREATED`
   （资源管理器重启）。`Services/NativeTrayIcon` 里已有一份可复用实现。
2. `H.NotifyIcon.WinUI`：省事，但要确认它的 AOT/裁剪兼容性，且多一个第三方依赖。
3. 不做托盘：与「后台常驻」定位冲突，不建议。

### 6.5 单实例与唤醒

WPF 版是 `Mutex` + 命名事件 + `FindWindow` 三段式（`SingleInstance` + `WakeWindow`）。
WinUI 3 换 `Microsoft.Windows.AppLifecycle.AppInstance`（已定，见 §9 决策 6）：

- `FindOrRegisterForKey("Zenwik")` 判重；
- 重复实例用 `RedirectActivationToAsync` 把激活转给主实例，**不用**再找窗口句柄；
- 好处：多实例、协议激活（`zenwik://`）以后都好加；代价：双击 exe 的唤醒路径变了，
  `-silent` 那条路要重新设计（见 §9 决策 8）。

### 6.6 发布形态

- WPF 用不了 Native AOT（`UseWPF` 与 `PublishAot` 互斥）。这正是迁 WinUI 3 的核心理由，
  但也意味着 **AOT 是否真的可行要在 WinUI 3 上从零验证**。
- 建议先做「非 AOT 的 WinUI 3 可运行版」，功能对齐后再开 `PublishAot` ——
  一次性两个变量（UI 框架 + AOT）同时动，出问题分不清是谁的。
- 未打包（unpackaged）+ 自包含 是最省事的组合：`WindowsAppSDKSelfContained=true` +
  `SelfContained=true` + `PublishSingleFile`。打包（MSIX）会带来签名与更新链路，本阶段不必。

---

## 7. 建议的迁移顺序

| 步 | 内容 | 验收 |
|---|---|---|
| 1 | 新建 WinUI 3 空壳，引用 `Zenwik.Core`，能读到 `ui.json` 并显示 CPU 名 | Core 原封不动能跑 |
| 2 | 调校页（只读）：SMU 快照 → 界面 | 数字与 WPF 版一致 |
| 3 | 调校页（写）：CO / FMax 下发 + 回读校验 | 写入后回读一致 |
| 4 | 进程页：`ProcessRefresher` + `ItemsRepeater` | 300 进程下列表可滚动、不卡 |
| 5 | 绑核监听：`ProcessWatcher` + 规则表 | 启动游戏自动绑核 |
| 6 | 日志页 + 设置页 + 托盘 | 关窗后进程常驻、内存与 WPF 版对比 |
| 7 | `PublishAot=true` 逐项排雷 | IL 警告清零，各功能回归 |

---

## 8. 已知风险与「不要照搬」的坑

1. **不要照搬 `SmoothSortPanel`**。它的做法是「布局值用新的、渲染位置从旧值补间」，依赖 WPF 的
   `RenderTransform` 与 `VirtualizingStackPanel` 的 `Arrange` 时机。WinUI 3 的 `ItemsRepeater`
   布局模型完全不同，硬搬会得到「行乱飞」。
2. **不要照搬 `Themes/Controls.xaml`**。1500 行模板里大量依赖 WPF 的 `Trigger` / `TemplateBinding` /
   `PART_*` 约定，逐条翻译的成本高于重写。
3. **`Application.MainWindow` 那类静态强引用陷阱在 WinUI 3 里同样存在**（`App.MainWindow` /
   静态 `Window` 字段）。关窗即销毁这条纪律要带过去。
4. **`System.Management` 是最大的不确定项**。AOT 下不可用时的替代方案是「降级轮询」
   （代码里已有这条路径，3 秒一轮），代价是新进程绑核延迟最多 3 秒。
5. **`-silent` 无界面模式**是场景自动切换的主战场。WinUI 3 下若不能在没有 XAML 的情况下跑
   （`App` 必须先初始化 XAML 框架），要把「后台调度」拆成独立进程或独立入口。
6. **`ZenStates-Core` 的 PawnIO 模块是嵌入资源**，AOT 裁剪要显式保留：
   `<TrimmerRootAssembly Include="ZenStates-Core" />` 或 `DynamicDependency`，
   否则运行时会报「PawnIO module not found」—— 这句话看上去像驱动没装，极易误判。

---

## 9. 决策记录（10 条已拍板）

| # | 事项 | 结论 | 实施落点 / 影响面 |
|---|---|---|---|
| 1 | 命名空间 | **改**。产品名 `Zenwik`，工程/程序集 `Zenwik` + `Zenwik.Core`，命名空间 `Zenwik.*` 与 `Zenwik.Core.*`，配置目录 `%AppData%\Zenwik`，界面标题与关于页统一显示 `Zenwik` | 已全量执行（v1.2.1）。Core 命名空间下沉一层；`AppPaths` 收口路径；`InternalsVisibleTo("Zenwik")`；GitHub 仓库同步更名为 `Motu-Z/Zenwik` |
| 2 | Core 是否允许加功能 | **允许**。WPF 线封版，Core 可继续写不依赖 UI 的逻辑 | `v1.1.x` 的后端待办（场景规则编辑器、更多触发源）在 Core 侧先行，WinUI 3 直接消费 |
| 3 | SMU 通道 | **升级到 1.93.1，改为本地 DLL 引用**。移除 `LLT.ZenStates-Core` 包，改 `libs/ZenStates-Core/` 的 `Reference` + `HintPath`（取 `net8.0-windows` 资产） | 已执行。`Cpu` 无参构造改 `Cpu(CoreOptions.Current)`；传递依赖断链，`System.ServiceProcess.ServiceController` 显式声明；`WinIo32.dll` 随 exe 落地 |
| 4 | AOT 下砍 WMI | **接受**，保留 3 秒轮询降级路径 | `ProcessWatcher` 的降级路径已在（WMI 不可用 → 3 秒轮询，且集合比对后才广播）。WinUI 3 版直接不引用 `System.Management` |
| 5 | 托盘 | **保留**，自写 `Shell_NotifyIcon` | §6.4 方案 1；`NativeTrayIcon` 的 P/Invoke 部分直接搬，菜单换自绘或 `MenuFlyout` |
| 6 | 单实例 | **换** `AppInstance` | §6.5；`FindOrRegisterForKey("Zenwik")` + `RedirectActivationToAsync`，`WakeWindow` 与命名事件整条链路删除 |
| 7 | 强调色 | **继续跟随系统**并自绘 | `ColorHsl` 的 HSL 推导算法搬过去；WinUI 3 侧读 `UISettings.GetColorValue(UserPrimaryColor)`，不套内置主题色板 |
| 8 | `-silent` 无界面常驻 | **保留**。优先尝试隐藏 `AppWindow`；不行则做成独立后台进程 | 先试「不创建 `Window`、只跑后台调度」的 `App` 实例（WinAppSDK 必须先初始化，但没有窗口也能跑调度）；退路是 `Zenwik.exe --worker` 独立进程 + 轻量 IPC。现在的 `Program.Main` 分流结构照搬即可 |
| 9 | 平滑排序 | **沿用**，做成常量 | 参数集中到一处常量（200ms 缓动、单屏位移上限按视口比例），换 `ItemsRepeater` 实现时先固定再用真机微调 |
| 10 | 术语表 | **按此固定**：`核心电压偏移` / `最高加速频率` / `电源策略控制` / `SMU 已联通`；`CCD`、`FMax`、`CO`、`PID`、`CPU`、`GPU`、`API` 保留英文 | 新增文案照此执行；日志与界面同口径，不再出现 `Normal` / `High` / `RealTime` 这类原文 |

---

## 附录 A：v1.2.1 改动清单

1. 产品更名 `ZenForge` → `Zenwik`（解决方案 / 工程 / 程序集 / 命名空间 / 配置目录 / UI 文案 / 图标 / GitHub 仓库）；
2. Core 命名空间下沉为 `Zenwik.Core.*`，WPF 层依赖改为两条全局 `using`；
3. `AppPaths` 收口全部落盘路径，附旧目录一次性迁移（`Move` → 复制 → 保留原样 三级退化）；
4. SMU 通道换 `ZenStates-Core` 1.93.1 本地 DLL（`net8.0-windows` 资产），适配 `Cpu` 构造变化；
5. `System.ServiceProcess.ServiceController` 显式包引用；`WinIo32.dll` 排除单文件、随 exe 落地；
6. 版本号三处同步至 `1.2.1`，本交接文档定稿 v1.0.1。

## 附录 B：一条命令验证红线

```bash
# 1) Core 单独编译（出现任何 System.Windows 类型就会在这里失败）
dotnet build Zenwik.Core/Zenwik.Core.csproj -c Release -p:EnableWindowsTargeting=true

# 2) 产物引用表里不应出现 WPF 程序集
strings Zenwik.Core/bin/Release/net10.0-windows/Zenwik.Core.dll \
  | grep -E "^(PresentationFramework|PresentationCore|WindowsBase|System.Windows.Forms|System.Drawing)$"
# 无输出 = 通过

# 3) 发布产物应当是两个文件
dotnet publish Zenwik.csproj -c Release -r win-x64 -o out/ && ls out/
# Zenwik.exe  WinIo32.dll
```
