# Zentwik 调校页 (TuningPage) 接口契约 (v1.1)

> **⚠️ 强制规范**：本文档是前端 (XAML) 与后端 (ViewModel) 通信的唯一法定契约。
> 任何 AI 或开发者不得擅自修改、重命名本文档中定义的属性或命令；如需修改，请提前与人类商讨并告知另一个 AI。
>
> **契约基线**：`v2.0.0-can15`（commit `3f928f7`，分支 `main`）。
>
> **契约版本记录**：
> - **v1.2（2026-11-01，人类批准）**：电源策略防抖收敛到 ViewModel —— 滑杆变更停手 700ms 自动执行、下拉立即执行。**`ApplyPowerCommand` 本身不变**（名称 / 来源方法 / 行为 / 绑定方式全部照旧），新增 View 层可调用的非绑定方法 `SchedulePowerApply()`（详见 4.1）。**前端无需改动 XAML**：既有事件处理器名称与签名保持不变。v1.1 立项的 `IsCoreParkingSupported` / `IsFMaxSupported` 两个硬闸门属性**均已实现**。
> - **v1.1（2026-11-01，人类批准）**：新增 `IsCoreParkingSupported` / `IsFMaxSupported` 两个能力闸门属性（详见 2.2 / 2.3），**由后端 AI 补全实现**（v1.1 当时代码尚无这两个属性，v1.2 起均已实现）。其余属性、命令、集合定义与 v1.0 完全一致，前端已有绑定不受影响。
> - **v1.0**：基于 `v2.0.0-can15` 真实代码反向生成。
> **反向生成来源**（已逐行核对，非推测）：
> - `Zentwik.App/ViewModels/TuningViewModel.cs`
> - `Zentwik.App/ViewModels/CoreSlotViewModel.cs`
> - `Zentwik.App/ViewModels/UiSeverity.cs` / `Zentwik.App/UiConverters.cs`
> - `Zentwik.App/Views/TuningPage.xaml` / `TuningPage.xaml.cs`
> - `Zentwik.Core/Services/ISmuReader.cs` / `SmuReader.cs`（SmuSnapshot 等数据结构）
> - `Zentwik.Core/Services/ProcessorPowerPolicy.cs` / `ProcessorPowerTuning.cs`
> - `Zentwik.Core/Helpers/TuningStore.cs` / `Zentwik.Core/Services/ProfileService.cs`

---

## 0. 页面装配方式（绑定前必须知道）

| 项 | 真实情况 |
| :--- | :--- |
| ViewModel 构造 | `TuningViewModel(ISmuReader smu, ITuningStore tuning, ProfileService profiles, ILogService log, IUiDispatcher dispatcher)` —— **全部构造函数注入 Core 服务**，UI 层禁止自行 new |
| 页面构造 | `TuningPage(TuningViewModel viewModel)` —— 外壳显式 new 并注入，**不走** `Frame.Navigate(typeof(T))` |
| 初始化入口 | `await ViewModel.InitializeAsync()`，由页面 `Loaded` 事件调用，**只执行一次**（切页回来不重读 SMU） |
| 绑定模式 | 全量 `{x:Bind}` 编译期绑定；默认 `Mode=OneWay`，输入控件显式 `Mode=TwoWay` |
| 线程模型 | SMU 访问在 ViewModel 内 `Task.Run` 到线程池，`await` 后续体回到 UI 线程；UI 层不做额外封送 |

---

## 1. 基础信息 (Static/Dynamic Info)

| 属性名 | 类型 | 默认值 | 说明 |
| :--- | :--- | :--- | :--- |
| `CpuName` | `string` | `"未探测"` | 如 "AMD Ryzen 9 9955HX"，由 SMU 快照回填。 |
| `CpuSubText` | `string` | `"等待同步 SMU"` | 如 "Raphael · SMU SMU 90.6.0"；SMU 未响应时为 "SMU 未响应"。 |
| `TopologyText` | `string` | `"—"` | 如 "2 CCD × 8 核 · 16 物理 / 32 线程"。 |
| `CoreSummary` | `string` | `"0 核"` | 汇总行：`"{N} 核 · 与硬件一致"` 或 `"{N} 核 · {dirty} 核待下发"`。 |
| `SmuStateText` | `string` | `"未连接"` | SMU 状态行文案："SMU 就绪" / 错误全文。 |
| `SmuAvailable` | `bool` | `false` | 最近一次 SMU 读取是否成功。**读取失败的可视化靠它 + 提示条，没有 `IsSyncFailed` 属性**（见 2.6）。 |

> **🚨 核心总数没有独立 int 属性。** `MaxCores` / `CoreCount` 在 ViewModel 中均**不存在**（`_coreCount` 是 private）。
> 物理核心总数 = `Cores.Count`（见第 3 节）。**前端严禁写死 16**——8 核机生成 8 格，16 核机生成 16 格，全部由集合驱动。

---

## 2. 状态属性 (State Properties)

### 2.1 脏数据 / 忙碌 / 提示条

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `IsDraftDirty` | `bool` | `false` | OneWay | 草稿与基准不一致。**当前唯一绑定用途**：方案下拉框右上角 `*` 角标显隐（`Visibility` 经 `UiConverters.ToVisibleWhenTrue` 转换）。 |
| `IsBusy` | `bool` | `false` | OneWay | 写入/读取中。为 `true` 时遮罩吞指针事件、禁用全部交互；同时令 5 个命令 `CanExecute=false`。 |
| `BusyText` | `string` | `"正在与 SMU 通信…"` | OneWay | 遮罩中央文案，随任务变化（如 "正在写入核心 3…"）。 |
| `IsStatusVisible` | `bool` | `false` | TwoWay | InfoBar 开关。**注意：后端只在 Error / Warning 时自动置 true**；Success / Info 不弹条。 |
| `StatusTitle` | `string` | `""` | OneWay | 提示条标题（如 "CO 下发失败"）。 |
| `StatusMessage` | `string` | `""` | OneWay | 提示条正文（失败明细为人话，含失败核号）。 |
| `StatusLevel` | `UiSeverity` | `Info` | OneWay | `Info / Success / Warning / Error` 四值；XAML 侧经 `UiConverters.ToInfoBarSeverity` 转 `InfoBarSeverity`。 |

> **脏数据浮动保存条的显隐不由 `IsDraftDirty` 绑定驱动**：它由 View 层 `MarkDirty() → ShowDirtyBar()`（180ms 淡入上移动画）与命令执行后 `HideDirtyBar()` 控制。契约上 View 必须：任何用户输入路径最终调用一次 `ViewModel.MarkDirty()`（见 5.2）。

### 2.2 FMax（最高加速频率）

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `FMaxText` | `string` | `"5400"` | TwoWay | 纯 TextBox 入口。**容错解析**：空串 / 非整数 / 负数直接忽略，不抛异常；合法值写入 `FMaxValue`。 |
| `FMaxValue` | `int` | `5400` | OneWay | 解析后的整型值（MHz）。 |
| `FMaxHint` | `string` | `"未同步"` | OneWay | 三种态：`"{n} MHz（硬件当前值）"` / `"{n} MHz（回读一致）"` / `"该平台未提供 FMax 读取通道"`。 |
| `FMaxMinimum` | `int` | `400` | OneWay | 只读常量，步进器 / 校验下限。 |
| `FMaxMaximum` | `int` | `5400` | OneWay | 只读常量，步进器 / 校验上限。 |
| `IsFMaxSupported` | `bool` | `false` | OneWay | **v1.1 新增，v1.2 起已实现**。FMax 读取通道是否受支持的硬闸门：`true` = 平台提供 FMax 通道。赋值时机：初始 `false`；每次 SMU 快照读取**成功后**按 `snapshot.FMaxMHz > 0` 赋值（与 `FMaxHint` 的判定同源）；读取失败保持上次结果不变。前端 Visibility / IsEnabled 一律绑此属性。 |

> **前端禁止解析 `FMaxHint` 文案判断支持性**（v1.0 时代的脆弱替代方案已废止）：硬闸门统一绑 `IsFMaxSupported`；`FMaxHint` 仅作辅助展示文案。
> **v1.2 起**：FMax 输入框（TextBox）的 `IsEnabled` 改绑 `IsFMaxSupported`。

### 2.3 电源策略

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `PowerAvailable` | `bool` | `true` | OneWay | 电源策略**整体**可用性。升频模式与核心停放**两者都读不到**时才为 `false`（此时下拉 / 滑杆应禁用）。 |
| `PowerPlanIndex` | `int` | `-1` | TwoWay | 电源计划档位索引，对应 `PowerPlanOptions` 下标。**-1 = 系统未暴露，下拉框必须停在未选中态，禁止假装选中"均衡"**。 |
| `PowerPlanHint` | `string` | `"未读取"` | OneWay | 当前检测到的档位文案 / 反推失败时的错误文本。 |
| `BoostIndex` | `int` | `-1` | TwoWay | 升频模式索引，对应 `BoostOptions` 下标（顺序固定：积极且高效 / 积极 / 已启用 / 高效 / 已停用）。读回 `无设置` 哨兵时也为 -1，提示走 `BoostHint`。 |
| `BoostHint` | `string` | `"未读取"` | OneWay | 选中项说明 / `"系统默认（未显式设置升频模式）"` / 错误文本。 |
| `ParkingValue` | `double` | `100` | TwoWay | 核心停放百分比，**Slider 专用**（拖动过程必然为小数）。 |
| `ParkingInt` | `int` | `100` | OneWay | 停放百分比整型，步进器语义；与 `ParkingValue` 幂等同步（取整后一致即不回写，防互相触发）。 |
| `ParkingText` | `string` | `"100"` | TwoWay | 纯 TextBox 入口。容错解析同 `FMaxText`；合法值钳制 `≤ 100` 后写入 `ParkingValue`。 |
| `ParkingHint` | `string` | `"未读取"` | OneWay | 随数值变化的说明文案；读取失败时为错误文本（如 "当前电源方案不提供核心停放设置。"）。 |
| `IsCoreParkingSupported` | `bool` | `false` | OneWay | **v1.1 新增，v1.2 起已实现**。核心停放是否受支持的硬闸门。赋值时机：初始 `false`；电源策略读取成功后按 `ProcessorPowerTuning.TryReadCoreParking` 的 `parkOk` 赋值；`PowerAvailable == false`（升频/停放整体不可用）时**必须**为 `false`；读取失败保持上次结果不变。前端 Visibility / IsEnabled 一律绑此属性。 |

> **前端禁止解析 `ParkingHint` 文案判断停放支持性**（v1.0 时代的脆弱替代方案已废止）：硬闸门统一绑 `IsCoreParkingSupported`。
> **v1.2 起**：核心停放那一行（Slider + TextBox）的 `IsEnabled` 改绑 `IsCoreParkingSupported`；升频模式那一行仍绑 `PowerAvailable`。
> 真实行为备注（供前端预判）：`IsCoreParkingSupported == true` 时 `ParkingValue` / `ParkingText` 必然已回填有效值；`PowerAvailable == false` 时升频模式与停放**两组**一并禁用，与两个闸门属性同时为 `false` 等价。

### 2.4 核心网格空态

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `HasCores` | `bool` | `false` | OneWay | 已取到核心列表。为 `false` 时 `ItemsRepeater` 隐藏、空态面板显示。 |
| `IsCoreGridEmpty` | `bool` | `true` | OneWay | `!HasCores`，随 `HasCores` 自动通知（`NotifyPropertyChangedFor`）。 |

### 2.5 方案选择

| 属性名 | 类型 | 默认值 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- | :--- |
| `ProfileIndex` | `int` | `-1` | TwoWay | 当前选中方案，即 `ProfileOptions` 下标（与 `ProfileService.Items` 一一对应）。**切换 = 把该方案 CO/FMax 填进界面草稿，不碰硬件**；切换后 `IsDraftDirty` 重置为 `false`。回填期间索引变化被抑制（`_suppressProfile`），不会被当成用户切换。 |

### 2.6 与模板示例的差异（防幻觉对照）

| 模板示例中的属性 | 真实代码 | 结论 |
| :--- | :--- | :--- |
| `IsSyncFailed` | ❌ 不存在 | 读取失败用 `SmuAvailable=false` + `StatusLevel=Error/Warning` + `IsStatusVisible` 表达 |
| `IsCoreParkingSupported` | ✅ v1.1 立为正契属性，v1.2 起已实现 | 赋值时机见 2.3；前端禁解析 `ParkingHint` 文案 |
| `IsFMaxSupported` | ✅ v1.1 立为正契属性，v1.2 起已实现 | 赋值时机见 2.2；前端禁解析 `FMaxHint` 文案 |
| `MaxCores` | ❌ 不存在 | 核心总数 = `Cores.Count`，见第 1 节警告 |
| `CoreParkingText` | 实为 `ParkingText` / `ParkingInt` / `ParkingValue` 三件套 | 见 2.3 |

---

## 3. 动态集合 (Collections)

| 属性名 | 类型 | 说明 |
| :--- | :--- | :--- |
| `Cores` | `ObservableCollection<CoreSlotViewModel>` | **动态生成**：SMU 快照 `CoreMargins` 有多少个物理核心就生成多少个 Slot。数量不变时**只回填硬件值、不清空重建**（防滚动位置跳动）；数量变化才 Clear + 重建。前端必须 `ItemsRepeater` + `UniformGridLayout`（列数 8，`ItemsStretch="Fill"`，`MinItemWidth=64`，`MinItemHeight=34`）自动流式布局。 |
| `BoostOptions` | `ObservableCollection<string>` | 升频模式文案，固定 5 项，顺序由后端 `ProcessorPowerTuning.BoostOptions` 决定（AOT 下不用 `Enum.GetValues`）。 |
| `PowerPlanOptions` | `ObservableCollection<string>` | 电源计划文案，固定 4 项（解除限制 / 温和 / 均衡 / 静音），索引即 `PowerPlanIndex`。 |
| `ProfileOptions` | `ObservableCollection<string>` | 方案名列表，与 `ProfileService.Items` 一一对应，**索引即身份**（下拉框只需 int SelectedIndex，不塞对象）。空列表时后端会塞一项占位 `"自定义方案"` 并把索引指 0，避免下拉框空壳。 |

### `CoreSlotViewModel` 字段定义（一个物理核心一个实例）

| 属性名 | 类型 | 绑定模式 | 说明 |
| :--- | :--- | :--- | :--- |
| `Index` | `int` | OneWay | 线性核序号（0 起）。回滚与日志定位核心的唯一标识。 |
| `IndexText` | `string` | OneWay | 两位序号 "00"、"01"，等宽显示用。 |
| `CcdText` | `string` | OneWay | 所属 CCD，如 "CCD0" / "CCD1"（按 `CoresPerCcd` 计算，取不到时兜底 8）。 |
| `HardwareValue` | `int` | OneWay | 最近一次 SMU 读回值；`int.MinValue` = 该核读不到（熔断 / 不支持）。 |
| `HardwareKnown` | `bool` | OneWay | `HardwareValue != int.MinValue`。 |
| `HardwareText` | `string` | OneWay | 带符号展示（"+18" / "-20" / "0"）；读不到为 "—"。 |
| `Value` | `int` | OneWay | 编辑值的整型本体。 |
| `EditText` | `string` | **TwoWay**（`UpdateSourceTrigger=PropertyChanged`） | 纯 TextBox 入口。**容错解析**：空串与非法输入（含输入中间态 "-"）直接忽略，绝不抛异常。 |
| `ValueText` | `string` | OneWay | 编辑值带符号展示。 |
| `IsDirty` | `bool` | OneWay | `!HardwareKnown \|\| Value != HardwareValue`——只有 dirty 的格子才值得下发。 |
| `RoundedValue` | `int` | OneWay | 下发 / 保存时读取的值（当前 = `Value`）。 |
| `ApplyCommand` | `IRelayCommand` | — | **单核写入**入口（VM 已提供；当前 XAML 未绑定，保留给逐核下发按钮）。失败只回滚这一核，不会把别的核拽回旧值。 |

> 方法（非绑定，后端内部 / View 不直接调）：`UpdateHardware(int)`（回填硬件值并清 dirty）、`LoadDraft(int)`（从配置文件载入草稿，`int.MinValue` 忽略）。

---

## 4. 命令 (Commands)

全部命令 `CanExecute = !IsBusy`（`CanRun`）。`IsBusy` 变化时逐个刷新可执行状态。

| 命令名 | 来源方法 | 触发时机 / 绑定方式 | 后端行为要求 |
| :--- | :--- | :--- | :--- |
| `RefreshCommand` | `RefreshAsync()` | CPU 卡片同步图标按钮，`Command=` 直接绑定 | `Task.Run` 读 SMU 全量快照并回填（含拓扑 / FMax / 逐核值）。回填**抑制**脏状态。SMU 不可用时不硬读，直接告警提示条。 |
| `ApplyCommand` | `ApplyAsync()` | CPU 卡片「应用」按钮，`Command=` 直接绑定 | 全程遮罩：①逐核 CO 越界校验（-50~+50，失败即停并报核号）→ ②读快照（回滚基线）→ ③逐核下发（未改动的核用 `int.MinValue` 哨兵跳过）→ ④回读校验（Core 完成）→ ⑤**任何失败整体回滚到下发前**并静默刷新回填 → ⑥成功后落盘 `tuning.json`、清脏状态。FMax 仅在用户改动过时才写。 |
| `ApplyPowerCommand` | `ApplyPowerAsync()` | 下拉换项时由 View 层**立即**调用；滑杆路径由 ViewModel 内部防抖自动执行（见 4.1）。命令本身未变 | `PowerAvailable=false` 时拒绝。顺序硬约束：**先电源计划（整套索引，会覆盖后两者）→ 再升频模式 → 再核心停放**。三项各自独立报告成败，部分失败也如实汇总。完成后回读刷新。 |
| `LoadCommand` | `Load()` | ⚠️ **当前 XAML 未绑定**（保留入口） | 把 `tuning.json` 已保存设定填进界面草稿，**不写硬件**。无记录时提示。 |
| `SaveCommand` | `Save()` | 浮动保存条「保存覆盖」按钮（View 层 `Click → Execute(null)`） | **只落盘不下发**。保存的是"期望状态"（逐核 `RoundedValue` + FMax + 出厂基线），供开机静默自启重新应用。无核心列表时拒绝。 |
| `DiscardDraftCommand` | `DiscardDraft()` | 浮动保存条「放弃」按钮（`Click → Execute(null)`） | 界面回到**当前选中方案**（`ProfileIndex` 对应快照）的 CO / FMax 值，**不碰硬件**；清脏状态。 |
| `SaveDraftAsCommand` | `SaveDraftAs()` | 浮动保存条「另存为」按钮（`Click → Execute(null)`） | 当前草稿（CO + FMax）存成新方案（名 "自定义方案 HHmmss"）并选中新方案，落盘，清脏状态。 |

> 模板示例名 → 真实名对照：`SyncHardwareCommand`→**`RefreshCommand`**；`ApplySettingsCommand`→**`ApplyCommand`**；`DiscardChangesCommand`→**`DiscardDraftCommand`**；`SaveProfileCommand`→**`SaveCommand`**；`SaveAsProfileCommand`→**`SaveDraftAsCommand`**。

### 4.1 电源策略的提交时机（v1.2 修订）

防抖逻辑已从 View 层收敛到 ViewModel —— 计时与提交必须同层，否则 View 与 ViewModel 各持一套计时器时会互相触发对方的提交路径。分工：

| 触发源 | 提交时机 | 路径 |
| :--- | :--- | :--- |
| 电源计划 / 升频模式下拉换项 | **立即** | View 层 `OnPowerSelectionChanged` → `ApplyPowerCommand` |
| 核心停放滑杆拖动 | 停手 **700ms** 后 | View 层 `OnParkingValueChanged` → `ViewModel.SchedulePowerApply()` → ViewModel 内部防抖 → `ApplyPowerAsync()` |

**`ApplyPowerCommand` 的名称、来源方法、行为、可执行条件全部未变**，仍可被 XAML 直接 `Command=` 绑定。

新增（**非绑定**）View 层可调用成员：

| 成员 | 类型 | 说明 |
| :--- | :--- | :--- |
| `SchedulePowerApply()` | `void` | 通知 ViewModel「用户改动了停放滑杆」。ViewModel 内部：挂起 700ms 计时、代次作废上一次挂起、到期自动执行 `ApplyPowerAsync()`。初始化回填期间与本机提交期间调用**静默忽略**（不报错、不排队）。**前端不需要在 XAML 里引用它** —— 由 View 层代码调用。 |

> **前端影响：无。** `OnPowerSelectionChanged` / `OnParkingValueChanged` 的名称与签名均未变化，XAML 绑定无需改动。

---

## 5. 交互约定 (Interactions)

### 5.1 多语言与样式
- 前端使用 `x:Uid` 绑定资源，**后端不硬编码中文字符串**（VM 内中文仅用于状态提示文案，属展示数据，不动它）。
- 数值输入：**纯 `TextBox`**（样式 `AppTextBoxMono`，34px / Consolas / 居中 / `MaxLength`），**不是** NumberBox——契约入口是 `EditText` / `FMaxText` / `ParkingText` 三个容错字符串属性。停放另有 `Slider`（0~100，步进 5）。
- 下拉框：必须用自定义 `ctl:AppComboBox`（深浅自适应），**禁止系统默认白底 ComboBox**。
- `bool → Visibility` 一律走 `UiConverters.ToVisibleWhenTrue / ToVisibleWhenFalse / ToCollapsedWhenTrue` 编译期函数；`UiSeverity → InfoBarSeverity` 走 `UiConverters.ToInfoBarSeverity`。**不引 IValueConverter 进资源字典**。

### 5.2 脏数据链路（View 层职责）
1. 任何用户输入路径（CO/FMax/停放 TextBox 打字、下拉换项、拖滑杆）最终必须调用 `ViewModel.MarkDirty()` 一次 → `IsDraftDirty=true` → `*` 角标显现。
2. 判定"是不是用户在操作"的门槛：TextBox 仅在**持有焦点**时才算用户输入（回填 / 容器延迟 realize 也触发 TextChanged，必须排除）；初始化回填全程压住（`_suppressUiDirty`）。
3. 命令成功 / 切方案后后端会清 `IsDraftDirty`，View 层同时 `HideDirtyBar()`。
4. `IsBusy=true` 期间忽略一切 MarkDirty。

### 5.3 提示条语义
- 只有 **Error / Warning** 会自动打开 InfoBar；Success / Info 只更新 Title / Message 不弹条（成功反馈不占首屏）。
- `IsStatusVisible` 是 TwoWay：用户点 × 关闭只是 UI 行为，后端状态不变。

### 5.4 遮罩语义
- `IsBusy=true` → 全页遮罩 + `ProgressRing` + `BusyText`；遮罩**吞指针事件**（SMU 写入期间任何输入都会被下一次写入带上）。
- 遮罩固定文案："若失败将自动回滚至原状态"。

### 5.5 架构红线（前端改动不得破坏）
- 全量 `x:Bind`，零反射；不得引入破坏 Native AOT 的写法。
- UI 层**禁止 new Core 服务**（尤其 `SmuReader`：进程内单例，被 Dispose / 重复创建会永久"打哑"SMU）。
- Core 已 AOT 验证：不得改动 `ISmuReader` / `SmuSnapshot` / `SmuWriteResult` / `SmuFMaxResult` / JSON 源生成上下文 / `[LibraryImport]` 封送。

---

## 6. 后端数据结构参考（前端只读了解，勿绑定）

**`SmuSnapshot`**（`ISmuReader.ReadSnapshot()` 返回）：`Success`、`Error`、`CpuName`、`CodeName`、`Family`、`SmuType`、`SmuVersion`、`Ccds`、`Cores`、`PhysicalCores`、`ThreadsPerCore`、`CoresPerCcd`、`CoreMargins`（`int[]`，`int.MinValue`=读不到）、`FMaxMHz`（0=未知）。

**`SmuWriteResult`**：`AllSucceeded`、`Fatal`、`WriteFailed`（`List<int>` 被拒核号）、`VerifyFailed`（回读不一致核号）、`Applied`（`Dictionary<int,int>` 成功核→值）。

**`SmuFMaxResult`**：`AllSucceeded`、`SmuAccepted`、`Requested`、`ReadBack`、`Fatal`。

**`TuningProfile`**（tuning.json）：`CurveOptimizer`（`int[]`）、`FMaxMHz`（`uint`）、`FMaxBaselineMHz`（`uint` 出厂基线）、`AppliedAt`（`string?`）。

**`ProfileSnapshot`**（profiles.json，实现 `INotifyPropertyChanged`）：`Id`、`Name`、`Note`、`CurveOptimizer`、`FMaxMHz`、`BindingRules`、`IsActive`（场景切换在后台刷新，UI 只需 DataTrigger）。

**值域红线**：CO ∈ [-50, +50]；FMax ∈ [400, 5400] MHz；核心停放 ∈ [0, 100] %。

---

## 7. 契约变更登记（新增属性必须走此流程）

**v1.2 变更记录（2026-11-01，人类批准）**：

| 提案项 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `SchedulePowerApply()` | `void`（非绑定方法） | 滑杆停手 700ms 后由 ViewModel 自动提交电源策略 | ✅ **已实现**（防抖由 View 层收敛到 ViewModel；`ApplyPowerCommand` 本身未变，前端 XAML 无需改动） |
| `IsCoreParkingSupported` | `bool` | 停放不支持时隐藏/禁用该行（硬闸门） | ✅ **已实现**（v1.1 立项，v1.2 落地） |
| `IsFMaxSupported` | `bool` | FMax 通道不支持时置灰输入（硬闸门） | ✅ **已实现**（v1.1 立项，v1.2 落地） |

**v1.1 变更记录（2026-11-01，人类批准）**：

| 提案属性 | 类型 | 用途 | 落地状态 |
| :--- | :--- | :--- | :--- |
| `IsCoreParkingSupported` | `bool` | 停放不支持时隐藏/禁用该行（硬闸门） | ✅ **已并入正文 2.3 并实现**（v1.2 落地；默认 `false`；电源策略读取成功后按 `TryReadCoreParking` 的 `parkOk` 赋值；`PowerAvailable=false` 时必为 `false`） |
| `IsFMaxSupported` | `bool` | FMax 通道不支持时置灰输入（硬闸门） | ✅ **已并入正文 2.2 并实现**（v1.2 落地；默认 `false`；SMU 快照读取成功后按 `snapshot.FMaxMHz > 0` 赋值） |

**仍在候选区（未批准，前端不得绑定）**：

| 提案属性 | 类型 | 用途 | 现状替代方案 | 状态 |
| :--- | :--- | :--- | :--- | :--- |
| `PhysicalCoreCount` | `int` | 空态 / 汇总文案直接取核数 | `Cores.Count`（等价，仅便利性） | 可选，不急 |

> 后续任何新增属性 / 命令 / 重命名，必须走本流程：人类批准 → 本文档登记（属性名、类型、默认值、绑定模式、后端赋值时机）→ bump 契约版本号 → 同步告知前后端两个 AI。严禁单方面改动。
