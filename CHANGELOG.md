# 更新日志

ZenForge 各版本改动记录。安装包与 Release 正文均以本文件为准。

## 版本号规则

版本号一律采用 `主.次.修订` 加可选后缀，后缀直接写在版本号里（如 `0.8.5-can1`），文件名、tag、Release 标题全部照抄，不改写、不省略。

**默认不带后缀。** 后缀只在有必要时才加，不要因为「还没验证」就顺手标 can。

| 后缀 | 含义 | 什么时候用 |
| --- | --- | --- |
| 无后缀 | 正式版 | 日常默认。不代表「绝对稳定」 |
| `-rc1`、`-rc2` | 预览版 | 功能更新但没改完，或可能不完全可用 |
| `-can1`、`-can2` | 测试版 | 仅当改动巨大、完全无法预计可用程度时才用 |

不设 `-fix` 后缀：修 Bug 就是普通迭代，版本号往前走一个修订号（0.8.6 出问题就发 0.8.7），不需要另外的后缀来标记。

`Version` 可以带后缀，但 `FileVersion` / `AssemblyVersion` 只能是四段数字。第四段用来记录「同一主.次.修订下第几次发版」，从 0 起算，按发布次序递增：

| Version | FileVersion / AssemblyVersion |
| --- | --- |
| `0.8.5-can1` | `0.8.5.0` |
| `0.8.5-rc1` | `0.8.5.1` |
| `0.8.5-rc2` | `0.8.5.2` |
| `0.8.5` | `0.8.5.3` |

后缀数字按同类递增：同一次改动反复出包就是 `-rc1`、`-rc2`，不跳号、不复用。第四段不与后缀数字对齐，只跟发版次序走 —— 一个 `-rc2` 的第四段是多少，取决于它之前发过几次，不是后缀里的那个 2。

程序内部（日志、关于信息）显示的版本取自 `AssemblyInformationalVersion`，带后缀；资源管理器「属性 → 详细信息」里的文件版本是四段数字。两者一对照就能确定这个包是第几次发版。

### 1.0.0-can9

修三件「用起来就是不行」的事：进程列表不刷新、部分进程设不了优先级、搜索框错位。

**1. 进程列表永远不刷新（日志满屏「调用线程必须为 STA」）**

采样本身一直是对的，错在「把数据送回去」的那一步。

`OnProcessesRefreshed` 由采样线程调用，方法体是：

```csharp
List<GroupedProcess> grouped = await Task.Run(() => GroupEntries(entries));
ApplySnapshot(grouped);      // ← 以为 await 之后回到了 UI 线程
```

`async` 只能回到「调用处的 SynchronizationContext」，而采样线程是线程池线程，
没有这个上下文 —— 于是 `ApplySnapshot` 仍在后台线程上执行，
去写绑定在 `ItemsControl` 上的 `ObservableCollection`，WPF 立刻抛
`The calling thread must be STA`。异常被 `catch` 吞成一条 WARN，
`finally` 又把重入标志清零，下一轮照旧失败。表现就是**每秒一条 WARN + 列表一动不动**。

两处一起改：

- `ProcessRefresher` 的采样循环从 `Task.Run` 改为**专用后台线程，显式 `SetApartmentState(STA)`**。
  采样与回调整条链路只在一条线程上串行发生，单元状态固定；
- 页面侧只在后台算分组，**回写一律 `Dispatcher.BeginInvoke`**；
  重入标志的语义也修正了：以前「忙就丢帧」，现在忙时记一笔，
  算完补最新那一帧，保证最终画上去的一定是新数据。

顺带：进页面时若首帧还没出来，主动催一次采样，而不是干等 3 秒。

**2. 某些进程设不了优先级（helldivers2.exe 报「权限不足或进程已退出」）**

页面与 `ProcessWatcher` 各有一份 `OpenProcess(0x0200) + SetPriorityClass`。
两份都只申请了 `PROCESS_SET_INFORMATION`。

`SetPriorityClass` 除了要求 `SET_INFORMATION`，内核还要判定目标是不是受保护进程，
这一步需要调用方能**查询**目标进程的安全信息。只拿 0x0200 的句柄在这个检查里被拒，
于是 `OpenProcess` 成功、`SetPriorityClass` 返回 `ERROR_ACCESS_DENIED(5)` ——
任务管理器能改、我们改不了，差的不是令牌，是句柄上的访问位。

现在统一到 `Services/ProcessPriority.cs`，申请
`PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION`，
失败时把 Win32 错误码原文带出来（「拒绝访问（错误 5）」「进程已退出（错误 87）」），
不再一律写成「权限不足或进程已退出」这种猜出来的话。

**3. 搜索框错位**

进程页的放大镜被占位文字盖住，日志页的输入起点偏右。根因是同一个数字被用在两个坐标系里：

| 位置 | 数值 | 所在坐标系 |
| --- | --- | --- |
| `TextBox.Padding.Left` | 32 | 输入框内部 |
| 占位符 `Margin.Left`（进程页） | 32 | 外层 Grid（左缘比输入框靠左 24） |
| 占位符 `Margin.Left`（日志页） | 32 | 已在内侧 Grid，再加一次 |

进程页因此左移 32（压到图标上），日志页因此右移 32（看起来从中间开始）。

按你的要求把两个页面的搜索框代码**整个删掉**重做：抽成 `zenforge:SearchBox`
控件 + `Themes/SearchBox.xaml` 模板，几何只在模板里存一份
（10 留白 + 14 图标 + 8 间距 = 32 内缩），占位符与输入框同格同内缩，
页面拿不到那些数值也就不可能再写错。
占位符一获得焦点即 `Collapsed`，不会再有「光标插在占位文字里」。

**4. 其它**

- 「核心停放」不可用时只留一句话：**当前电源方案不支持核心停放，此项已停用**。
  原始错误改为只进日志（之前它被拼进 UI，屏幕上出现
  「无法读取（当前电源方案不提供核心停放设置。），此项已停用」这种嵌套括号）。
- 「占用」列排序按显示精度（0.1%）取整后再比，显示相同数值的行不再来回换位。
- 「核心掩码」列 120 → 150、「优先级」列 110 → 128，自定义掩码名不再被切掉半个字。
- `tools/xamlcheck.py` 新增第 4 类检查：自定义控件上的属性/事件名是否真实存在
  （连带修复了命名空间剥离 —— 之前 `{clr-namespace:...}SearchBox` 永远匹配不上，
  这条检查其实是「假绿」）。

### 1.0.0-can8

修 can7 的两处运行时崩溃（编译期 0 警告，一打开就炸），并把进程表列宽改为 3:7。

**1. `Grid.ColumnDefinitions` 引发 InvalidCastException，整页加载失败**

把「核心停放」的可编辑区从 `Grid` 改成 `StackPanel` 时，只改了开标签，
里面的 `ColumnDefinitions` 与两个 `Grid.Column="n"` 全部留着。
`StackPanel` 没有 `ColumnDefinitions` 这个属性，BAML 一加载就抛
`Unable to cast object of type 'StackPanel' to type 'Grid'`，
整个调校页白屏。

改回 `Grid`（这块本来就是「左文字 / 右控件」两栏，必须是 Grid），
并给它加一行 `RowDefinitions`，把端点说明放到第 1 行跨两列 ——
这样说明在标题下方，不再是之前的倒序。

**2. `"10"不是属性"Width"的有效值`**

把搜索图标的几何尺寸抽成 `sys:Double` 资源给
`ColumnDefinition.Width` 用，运行时抛 `ArgumentException`。
根因是 `ColumnDefinition.Width` 要的是 `GridLength`，
而 `GridLength` 是结构体，没有从 `Double` 的隐式转换；
XAML 标记扩展不做跨类型强转。

修法：列宽与图标尺寸直接写字面值，公式集中写在 `App.xaml` 的注释里
（内缩 = 左留白 10 + 图标 14 + 间距 8 = 32）。
不再为了「少写一个数」引入一层运行时类型转换风险。

**3. 进程表列宽 3:7**

进程名 / 路径从 `1* / 4*`（20% : 80%）改为 `3* / 7*`（30% : 70%）。
原比例下进程名只有 20%，`L-Mechrevo.exe` 这类 14 字符的名字被截成
`L-Mechrevo....`，而路径本来就靠 ToolTip 看完整值，不需要 80%。
表头与数据行三处 `ColumnDefinitions` 同步改。

**4. 新增 `tools/xamlcheck.py`**

这一版连着两次因为「容器与属性不匹配」在运行时崩溃，而编译期全绿 ——
说明只靠编译和肉眼过 XAML 不可靠。加一个静态检查脚本，三条规则：

- 资源类型与属性类型是否匹配。难点在 `Width` 同时属于
  `FrameworkElement`（`Double`）与 `ColumnDefinition`（`GridLength`），
  只按属性名判断会漏检，必须结合父元素类型 —— 这一条最初写漏了，
  用测试用例验证后才补上。
- 容器结构：`StackPanel` 里出现 `Grid.ColumnDefinitions`、
  非 Grid 父级上写 `Grid.Row` / `Grid.Column`、`Border` 多个子元素、
  `TextBlock` 写 `Padding`。
- XML 可解析性。

有输出即返回非 0，可直接接进构建流程。

### 1.0.0-can7

修复 can6 引入的三处回归，全部来自「加了个显式样式却没 BasedOn」。

**1. 下拉框退化回系统浅灰控件（最严重）**

新增的 `TableCellComboStyle` 写了 `TargetType="ComboBox"` 但没写 `BasedOn`。
WPF 的规则是：一个带 `x:Key` 的样式只要 `TargetType` 与控件匹配且被显式引用，
就**完全接管**该控件，全局隐式样式（深色模板）不再参与。
于是表格里所有下拉框丢掉了模板，退回系统默认的浅灰 ComboBox ——
深色界面上是一片刺眼的白块，下拉箭头、内边距、字号全部还原成系统值。

这是同一类错误的第三次（前两次分别是 ScrollBar 与自定义按钮）。
修法：`BasedOn="{StaticResource AppComboBoxStyle}"`，只覆盖尺寸，模板与配色继承。

同时补了一道静态检查：凡 `TargetType` 命中"已有隐式样式"的类型、
却不带 `BasedOn` 的显式样式，全部列出来人工过一遍
（自带完整 `Template` 的除外，如 `HueSliderStyle` / `CoreCheckBoxStyle`）。

**2. 搜索框光标落在占位文字里**

两个问题叠在一起：

- 布局：放大镜 `Path` 和占位 `TextBlock` 都用 `HorizontalAlignment="Left"`
  加写死的 `Margin` 叠在 `TextBox` 上。同一件事（"文字从哪开始"）
  存了三份数值：`TextBox.Padding.Left`、`Path.Margin`、`Placeholder.Margin`。
  三者不等时，光标起点与占位文字起点就错开，视觉上光标插进了文字中间。
- 逻辑：日志页的 `UpdateSearchPlaceholder` 只判断 `Text` 为空，
  没判断焦点。点进空的搜索框 → 有焦点、无内容 → 占位符仍然显示 → 光标压在它上面。

改成：图标占 `Grid` 一列、输入框占一列，真实布局保证两者永不重叠，
内缩只剩 `Padding="32,0,10,0"` 一个来源（= 左留白 10 + 图标 14 + 间距 8，
三个数抽成 `SearchIconInset` / `SearchIconSize` / `SearchIconGap` 资源）。
占位符左缩用同一个 `SearchBoxTextInset` 资源，并补上焦点判断；
`LostFocus` 延后一拍刷新，因为 `IsKeyboardFocusWithin` 在事件触发时尚未更新。

**3. 「核心停放」标题跑到说明下面**

`ParkingScaleHint`（"0% 允许全部核心停放 · 100% 禁止停放"）写在了两个状态面板
**之外**，而"读不到"的面板在它下面。于是不可用时屏幕上呈现出
"解释文字 → ⚠ 核心停放"的倒序。

改成两态严格互斥：提示归入可编辑态内部，"读不到"态只有"标题 + 原因"一行。
**没有功能就不出现任何解释功能怎么用的文字。**

**4. 文案与内容清理**

- 「已保存规则」徽章从「17 条规则」改为「17 条」—— 标题已经说了"规则"。
- **配置页整页演示内容删除**。上一版画了三张卡并填满
  「cs2.exe → 方案 B：极限游戏」「CO -20 · FMax 4.5G」「QQ.exe → CCD1」
  这类写死的示例。它们看起来太真实，用户会以为规则已生效，
  然后去找"为什么改了没反应"。现在只有一张空状态卡：一句"将来做什么"+
  一句"为什么现在没有"，没有控件、没有示例、没有可点元素。

### 1.0.0-can6

全工程 UI 返工（间距 / 字体层级 / 组件高度三套口径），并把几乎全部阻塞式弹窗改为 Toast。
另修一个 can6 首包启动即崩溃的资源键冲突。

**0. 启动崩溃：`Text.Meta` 同时是画刷和样式（can6 首包）**

首包在窗口 Measure 阶段抛 `InvalidOperationException：“System.Windows.Style”不是属性“Foreground”的有效值`，
主窗口永远出不来，第二次启动也只会走到「唤出主窗口失败」。

根因是两个字典定义了同一个键：调色板里 `Text.Meta` 是 `SolidColorBrush`（46% 白），
`Typography.xaml` 里 `Text.Meta` 是 `TextBlock` 的 `Style`。
`MergedDictionaries` 按声明顺序装填，后装的 `Typography.xaml` 把画刷顶掉，
于是 `Foreground="{DynamicResource Text.Meta}"` 拿到一个 `Style` 对象。

编译期查不出来：`MarkupCompilePass1` 不解析跨字典的键类型，
`StaticResource`/`DynamicResource` 都是运行时才解析，BAML 照常生成、0 警告 0 错误。

修法是画刷改名 `Text.MetaBrush`（与既有 `AccentBrush` / `AccentSoftBrush` 命名对齐），
`Style` 保留 `Text.Meta`。同时补了一道静态检查：所有 `Style` 类型的键，
只能出现在 `Style=` / `BasedOn=` / `*Style=` 这类属性上。

**1. 间距与对齐**

- 侧栏从贴窗口边缘的整条改为浮起面板：独立底色、四角 12 圆角、四周留 8。
  导航项圆角由 `0,6,6,0` 改为完整四角 8，选中竖条收进内部不再被裁。
- 设置页原本并存三种行结构（`StackPanel` 堆叠 / 裸 `TextBlock` / 三层嵌套 `Grid`），
  统一为 `Grid(左* / 右Auto)` + `Stack.LabelBlock`。层数不同则垂直居中参照不同，并排看基线是歪的。
- 行距全工程收敛到 20 一个数，最后一行改用 `CardRowLastStyle` / `SettingRowLastStyle` 归零。
- 卡片圆角 8 → 10，内边距 `20,24` → `20,22`。

**2. 字体层级（新文件 `Themes/Typography.xaml`）**

七个层级键：L1 页面标题 26 / L2 卡片标题 15 / L3 核心数值 16 等宽 / L4 主标签 13 /
L5 说明 12 60% / L6 元数据 11 46% / L7 微标签 10。
旧键名（`CardTitleStyle` / `PageTitleStyle` / `CardRowLabelStyle` …）全部 `BasedOn` 新层级，
页面不必改引用，将来调层级只动一处。

单独成册的原因：`StaticResource` 只查「本字典 + 本字典合并的字典」，不向上查父字典，
兄弟字典互相看不见。`Controls.xaml` 里的样式要 `BasedOn` 这些键，
就必须放进一个 App 与 Controls 都 Merge 的独立字典。

- 全部窗口与页面开启 `TextFormattingMode="Display"` + `TextRenderingMode="ClearType"`
  （`Ideal` 按字形轮廓做亚像素定位，13px 中文在 100% 缩放下发虚）。
- 数值一律等宽字体：FMax 输入框、CO 单元格、色值框、日志时间戳。
  `-28` 与 `0` 在比例字体下宽度差近一倍，一列数字会参差不齐。
- `Text.Disabled`（30% 白）不再用于元数据，改走新增的 46% 白档 —— 原对比度约 2.6:1，读不出来。

**3. 组件高度只有两档**

输入框 / 下拉框 32，行内按钮 28，开关 22。清掉了散落的 30 / 34 / 36 / 38。
表格内下拉收进 `TableCellComboStyle`（进程页原本五处各抄一遍）。
日志页 4 枚 pill 与 3 个工具按钮同为 28，同一条水平线。
按钮选中态只换填充不换边框 —— `BorderThickness` 从 1 改 2 会让文字左右各移 1px，看起来在抖。

**4. 阻塞式弹窗清理（27 处 → 8 处）**

判据：能一句话说完 + 不需要用户当场决定 → Toast；破坏性操作 → 保留二次确认。
剩下 8 处全是后者（删掩码 / 删规则 / 清空规则 / 恢复出厂），外加两处启动前硬阻断
（缺少 .NET 运行时、Dispatcher 未处理异常）—— 那时主窗口还没建，Toast 没有宿主。

长失败清单（恢复出厂的部分失败项）不再用模态列出，改为一句 Toast + 逐条写日志页。

**5. 局部修复**

- 右键菜单项加 4px 圆角与 `Margin="4,1"`，否则高亮块方角会顶破菜单的 6px 圆角。
- 进程页警告条的 ⚠️ emoji 换成矢量三角 —— Windows 上 emoji 走彩色字形，与正文基线和字号都对不齐。
- 「核心停放」读不到时整卡折叠成一行提示，不再显示拖了没反应的滑块；
  错误按「权限 / 不存在 / 其它」三分流成可操作的说法。
- 日志行去掉无信息量的方框边框，改为 2px 级别色条（色条进 `Grid` 列，避免被 `Border` 圆角切断首尾）。
- 输入框底色从 `Input.Background`（20% 黑，比卡片还暗，像挖了个洞）改为新增的 `Control.Background`（6% 白）。
- 深色册按钮底色整体压暗，从「四个发亮方块」改为克制中性。

### 1.0.0-can5

两个处理器级新旋钮、优先级可下发、配置页界面落地，以及日志页与设置页返工。
另修正上一版误打成自包含、体积膨胀到 64 MB 的发布错误。

**0. 打包修正：64 MB → 1.16 MB**

`SelfContained` 在 csproj 里写的是 `false`，但发布命令带了 `--self-contained true`，
命令行覆盖项目设置，把整个 .NET 10 桌面运行时（解压后约 155 MB）塞进了单文件。

修正后两条约束固化进项目文件：发布一律 `--no-self-contained`；
`DebugType` 在 Release 下强制 `none`（原本 `embedded`，符号表跟着程序集进单文件白占体积）。
结果 `ZenForge.exe` 1.16 MB / zip 509 KB，与 0.8.x 同量级。

**1. 升频模式与核心停放（新文件 `Services/ProcessorPowerTuning.cs`）**

与四档电源策略（`ProcessorPowerPolicy`）**并列的独立旋钮**，互不覆盖。

`PERFBOOSTMODE` 写的是 Windows 电源计划里的处理器性能提升模式，五档：
积极且高效 4 / 积极 2 / 高效 3 / 已启用 1 / 已停用 0。
坑在于它是**枚举不是位标志**：4 看着像位组合的结果，但用 `[Flags]` 按位或算出 6 写进去，
系统当非法值忽略。所以枚举刻意不标 `[Flags]`。

`CPMINMAX` 是**范围型索引**，注册表里三条 DWORD：`Attributes=1` / `Min` / `Max`。
只改 `Max` 不改 `Min`，新 `Max` 一旦小于旧 `Min` 范围就非法，Windows 不报错、**静默回落默认**。
因此 `Min` 与 `Max` 永远同写同改。
另需区分实时值与意图值：`PowerReadACValueIndex` 读到的是内核按当前负载算的实时值，
用户设定值只在 `HKLM\...\Control\Power\PowerSettings\<子组>\<索引>` 下。

注册表走 `Microsoft.Win32.Registry` 而非 P/Invoke `advapi32`——`RegQueryValueEx`
的 type / size 两个 out 参数填错会静默读到 0。

**2. 进程优先级可下发**

优先级由只读文本改为下拉（与核心掩码一致）。运行中进程选中即生效；
已保存规则改完点「应用」，`ProcessWatcher` 在新实例启动时按规则补下发——
否则「设了 High → 重启游戏」优先级永远不会被重新应用。

`BindingRule.Priority` 默认空串表示「不碰优先级」，旧配置升级后不会被强改成 Normal。
`RealTime` 不提供：它高于系统输入线程，一个忙等进程就能让鼠标键盘失去响应。

**3. 配置页界面（仅版式，功能未实装）**

场景自动切换（总开关 + 触发规则列表）、总体配置方案（列表 + 保存 / 切换 + 当前生效标识）、
调校规则库与调度规则库双列且**严格等高**（`VerticalAlignment="Stretch"`）。
虚线新建按钮用 `Rectangle` + `StrokeDashArray="3 3"`，`Border` 不支持虚线边框。
所有交互走 `NotImplemented_Click` 只弹提示。

**4. 日志页**

- 三个按钮移到 pill 同一行（同一 Grid 两列，天然等高）。
- 「暂停」改为**暂停日志记录**，且是**真暂停**：`Write()` 入口直接 return，文件也不再写。
  旧版只冻结界面、磁盘照写。`Critical` 例外——程序要坏了不能静默丢日志。
- 卡顿根因不是渲染，是每来一条日志就全量遍历重算级别计数。改为写入路径增量计数，O(1)。
  上限 2000 → 800，裁剪批次 256 → 128。
- 列序「级别 → 时间 → 正文」，时间列 84 → 78。

**5. 设置页**

主题模式与强调色并排一行，各带说明；仅强调色选「自定义」时在第二行展开滑杆与输入框。
`CardTitleStyle` 统一加 `Margin="0,0,0,18"` 拉开标题与正文，行距统一 22。

**6. 其它**

- 托盘右键菜单改实色：`ContextMenu` 的 Popup 是 `AllowsTransparency=True`，
  系统不在其后画任何东西，用 `Card.Background`（3% 白）等于直接透出壁纸。
  新增 `Menu.Background` / `Menu.Border`。
- 警告标识贴到最左。
- 「应用全部」去掉确认弹窗，改 Toast —— 操作可逆且有回读校验，弹窗只是挡路。
- CPU 名拆分：主行 `AMD Ryzen 9 9955HX`，副行 `16-Core Processor · SMU 108`。
  9 组真实型号验证，含型号数字与核心数粘连（`7945HX16-Core`）不拆的情况。
- 副行里的 `SMU 版本` 降级为参考信息。它能证明 SMU 通道通着，但**不能用来判断 CO / FMax 是否生效**——
  同主版本 SMU 在不同主板上的 CO 可用范围、FMax 上限可以完全不同。
  要确认写入落地，看回读校验和实际频率。

### 1.0.0-can3

色彩解耦 + 崩溃修复 + 四项 UI 交互重构。本版最重要的一件事不是加了什么界面，
而是把「强调色」和「语义色」彻底分成两套互不影响的东西。

**1. 修复：点日志页即闪退（XamlParseException: 找不到 SearchBoxStyle）**

根因不是样式缺失，是 `StaticResource` 查找作用域错了：

`Themes/Controls.xaml` 里的 `SearchBoxStyle` 写了
`BasedOn="{StaticResource AppTextBoxStyle}"`，而 `AppTextBoxStyle` 定义在 `App.xaml`。
`App.xaml` 的合并结构是兄弟关系（`Accent.xaml` / `Palette.Dark.xaml` / `Controls.xaml`），
`StaticResource` 只查「本字典 → 本字典合并的字典」，**不向上查父字典**，
于是拿到 `null` 并抛异常。编译期完全无警告，只在日志页实例化时炸。

同样的写法在 `TuningPage.xaml` / `MaskManagerWindow.xaml` 里却不崩 —— 因为它们位于
`Page.Resources` / `Window.Resources`，属于元素树，查找能回溯到 `App.Resources`。
全项目 4 处引用，只有写进独立字典那一处失效。

修法：把 `SearchBoxStyle` 整体搬到 `App.xaml`（同字典内 `BasedOn` 合法），
`Controls.xaml` 原位置留注释说明这条规则。BAML 已核对：`App.baml` 含该键，
`Themes/Controls.baml` 不含。

**2. 色彩解耦：语义色与强调色彻底分离**

规范：**语义色优先级绝对高于强调色**。用户自定义的强调色只能用于交互元素
（按钮、滑块、选中高亮、焦点边框）；警告、错误、日志级别这些状态颜色必须固定。

- `Palette.Dark.xaml` 硬编码四个语义色，与 Accent 无任何关联：
  `Log.Info` `#8B9BB4`（中性灰蓝，避免与新强调色撞色）、
  `Log.Warn` `#F5D90A`、`Log.Error` `#FF453A`、`Log.Critical` `#C42B1C`。
- 新增 `Log.Critical` / `Log.CriticalTint` 键（深浅两册各一份，键名严格对齐，已校验 59/59 一致）。
- `LogLevel` 增加 `Critical` 级别（显示为 `FATAL`），异常兜底改用 `LogService.Critical`，
  并发时立刻冲盘。
- `ThemeService.SetAccentBrushes` 只允许写 5 个 `Accent*` 键，新增 `WriteAccent`
  双重校验：语义色黑名单 + 强调色白名单，写错直接抛 —— 让「手滑改语义色」
  在开发期就炸，而不是发布后让用户看见一堆跟着主题变色的告警。
- 日志页四枚 pill 由「调试/信息/警告/错误」改为「信息/警告/错误/致命」：
  调试级常规运行下永远是 0，占着最显眼的位置却没有内容。
- 顺手修掉两个统计/配色 bug：`UpdateStats` 原先用 `default: info++` 兜底，
  `Critical` 会被静默算进「信息」；日志行徽章底色原为写死的
  `#1FFFFFFF` / `#2EFFD60A` / `#2EFF453A`，现全部改走 `Log.*Tint` 资源键。

**3. 强调色：默认换 `#38BDF8`，主按钮改 v2 淡底方案**

- 默认强调色 `#60CDFF` → `#38BDF8`，共 6 类落点全部同步（漏一处就掉色）：
  `Accent.xaml` 五键、`ThemeService.DefaultAccentHex`、`SettingsPage.DefaultAccent`、
  `UiSettingsStore.CustomAccentHex`、`App.xaml` 的 `SystemColors.HighlightBrushKey`、
  `SettingsPage` 色板样本。
- `AccentSoftBrush` 改为 15% Alpha（`0x26`）。原先 `ThemeService` 里算的是 `0x2E`（18%），
  只改 XAML 不生效 —— 用户一改主题就被重算回去。
- `PrimaryButtonStyle` 从「实底强调色 + 深色字」改为「15% 淡底 + 强调色字 + 1px 强调色边」，
  悬停反转为实底。同时补上基类模板缺失的 `BorderBrush` / `BorderThickness`
  `TemplateBinding`（不补的话边框永远不显示）。

**4. Toast：普通反馈不再用模态弹窗**

新增 `Services/ToastService.cs` + `Controls/ToastHost.xaml`，主窗口顶部居中叠加层
（`IsHitTestVisible=False`，绝不挡住底下按钮），200ms 滑入 + 3 秒淡出，鼠标悬停时延长驻留。

判据只有一条：**用户必须读完并做决定吗？** 是 → 模态；否 → Toast。

改为 Toast 的（共 9 处）：保存配置、加载配置、应用成功、PBO 策略、SMU 自检完成、
规则已保存、应用完成、应用规则、启用/禁用规则、清空规则的空状态提示。
仍然保留模态的：全局重置、参数严重越界、写入失败、PBO 写入失败、未处理异常、删除确认。

**5. 侧栏指示条贴边**

原来有两层内缩叠加：`NavPanel` 的 `Margin` 左 12px ＋ 模板里竖条的 `Margin` 左 6px
= 距边 18px；而且选中时文字还会右移 8px「给竖条让位」，那才是「挤占左边距」的观感来源。

现在：`NavPanel` 左边距归零，竖条所在列固定 3px 宽、零边距，文字位置恒定不受选中态影响。
选中背景块的圆角改为只留右侧（`0,6,6,0`），避免左缘出现两个被边界切掉的圆角缺口。

**6. CPU 信息卡重排 + SMU 重读改图标按钮**

两列 Grid（`*` / `Auto`），左列两行文字垂直居中，右列四个控件横排：
`[加载] [保存] [⟳] [应用全部]`。窗口拉窄时先压缩 CPU 名（`TextTrimming` 兜底），
四个按钮永远完整可见。

刷新按钮从页脚文字链（`LinkButtonStyle`）改为 32×32 图标按钮（新增 `IconButtonStyle`），
图标是矢量圆圈箭头（`Viewbox` 缩放，直接改 `Path` 的 `Width/Height` 只会裁切不会缩放笔画）。

**7. PBO 卡片：走 Windows 电源策略，不是 SMU**

FMax 下方新增 PBO 策略卡片，下拉框 Auto / Enabled / Disabled，已接事件。

- **不做**：AMD SMU 固件层 PBO（功耗墙 / 曲线优化 / Boost 上限）。那一层没有公开 API，
  硬发 SMU 消息属于拿用户机器赌博（消息号或载荷错一位就是黑屏 + 清 CMOS）。
- **做**：Windows 电源策略里的「处理器性能提升模式」
  （`GUID_PROCESSOR_PERF_BOOST_MODE`），新增 `Services/ProcessorPowerPolicy.cs`，
  用 `PowerReadACValueIndex` / `PowerWriteACValueIndex` / `PowerWriteDCValueIndex`
  读写 AC 与 DC，再 `PowerSetActiveScheme` 让设置立即生效。
- 卡片说明行如实写明「与 SMU 固件层 PBO 为两套机制」，不误导用户。
- 读不到就置灰并写出原因，不做「假装能改」的开关；写入失败回滚下拉框并弹模态。

**8. 掩码管理窗口重绘**

780×540，深色卡片（`#121214`、圆角 10），标题栏圆形图标 + 标题 + 副标题 + 关闭按钮，
主体左栏固定 220（列表 + 新建），右区自适应（快捷按钮 + 勾选区 + 取消/保存）。

核心勾选框改为自绘深色方块（16×16、圆角 4、未选 1px 灰边、选中强调色实底 + 深色勾、
整行可点）。**保留 `CheckBox` 类型，只换 `ControlTemplate`** —— 代码里有 6 处
`foreach (CheckBox box in _allBoxes)`，换成 `ToggleButton` 子类会让这些循环全部编译失败。

**9. 全局异常兜底补齐两条通道**

原先只有 `DispatcherUnhandledException`（UI 线程）。补上
`AppDomain.UnhandledException`（进程级，只落盘不弹窗）与
`TaskScheduler.UnobservedTaskException`（`SetObserved` 阻止静默吞掉）。
异常弹窗加了递归闸门，防止「弹窗自身抛 → 再弹窗」的 `StackOverflow`。

### 1.0.0-can2

界面重做完成版：按 v12 视觉稿把五个页面全部落到纯原生 WPF。大版本号从 0.x 跳到 1.0.0，
因为这一轮改的是整体外壳、全部页面的版式、以及主题 token 的全量替换，不是增量修补。

**硬约束（本轮第一条验收标准）**

全程零 WebView2 / WebBrowser / 任何网页嵌套。布局只用 `Grid` / `Border` / `StackPanel`
/ `WrapPanel` / `UniformGrid`，颜色与圆角全部落在 `ResourceDictionary` 里。
grep 结果是 0 处命中的 `WebView2|WebBrowser|WebView`。

**版本号**

`Version` = `1.0.0-can2`，`FileVersion` = `1.0.0.2`，`AssemblyVersion` = `1.0.0.0`。
第四段记的是「1.0.0 下的第 3 次出包」（step1 用了 .0，上一轮 0.10.0 也算一次迭代，这里按发版次序落到 .2）。

---

#### 第二步：五页版式全部按 v12 重排

**调校页**

CPU 信息卡改成「左身份 / 右按钮」两列：18px 600 字重的 CPU 名 + 13px 副行
（`N 核 · M 个 CCD · 每 CCD K 核`），右侧三按钮改成「保存 / 加载 / 应用全部」。
第四个按钮「刷新」在 v12 里没有位置，但它承担的「把 SMU 现值读回界面」这个动作不能丢，
所以降级成页脚的一条文字链，不占卡片版式。

Curve Optimizer 从「StackPanel + 代码里逐行拼 Grid」改成 `<UniformGrid Columns="8"/>`。
**这是本轮唯一一处确定会跑崩的地方**：`CoreGrid` 的元素类型从 `StackPanel` 变 `UniformGrid`
之后，原代码里的 `line.ColumnDefinitions.Add(...)` 和 `Grid.SetColumn(box, c + 1)` 全部失效。
`BuildCoreGrid` 整体重写：所有格子平铺进去，填充顺序严格等于核心号 `0..n-1`，
`CoreBoxes` 的下标就是核心号，`ApplySnapshot` / `CollectMargins` 的对位逻辑一行没动。
CCD 标签行取消，改由网格下方的提示文字说明拓扑。

**UniformGrid 没有 RowSpacing** —— 行间距只能靠格子自身的 `Margin` 制造。
所以每个框的 `Margin` 是按位置算的：横向留 6、纵向留 8，最后一列和最后一行不留，
否则外沿会多出一截、跟卡片内边距对不上。

新增 PBO 占位卡，恒为 `Visibility="Collapsed"`。各代 SMU 的 PBO 消息号与载荷差异极大，
写错可能直接黑屏，必须先做能力探测。探测没实现之前，这张卡就是占位，不做「猜测式写入」。

**进程页**

整页抛弃 `ListView + GridView`，改自绘 `ItemsControl` + `Grid`。

理由不是审美：v12 的表头是「12px 次要色小字、无底无边、与数据行同样的 1.5fr / 2fr 弹性配比」。
`GridView` 的列头模板做不到弹性配比，还得额外写列宽同步代码 —— 上一版那套
`ApplyColumnWidths` + `RequestColumnWidthsUpdate` + 尺寸去抖（`_lastWidthForColumns`）
就是为这件事存在的，而它的注释里已经写满了「名称列封顶 170、增量全给路径列」这类补丁。
换成共享列定义之后，列宽天然一致，那段代码整体删除。

五列：进程名 / 路径 / 占用 / 核心掩码 / **优先级**。

优先级列是**只读**的。`PROCESSENTRY32.pcPriClassBase` 是从 Toolhelp 快照白拿的字段，
不需要 `OpenProcess` 额外权限，对受保护进程也能读到值，成本为零 —— 所以取它没有风险。
但 `SetPriorityClass` 不一样：把任意进程提到 `High` 以上，如果它对 CPU 是忙等型，
会把桌面响应一起饿死，表现就是整机卡死。所以这一版只把真实值显示出来，不给改的入口。

`ProcessRefresher.Entry` 新增 `PriorityClass` 字段，`ProcessMember` / `ProcessGroupRow`
各加一个对应字段与 `PriorityText`，映射表是 `pcPriClassBase → Idle / Below Normal /
Normal / Above Normal / High / RealTime`，未知值原样带出，不猜。

排序入口从 `GridViewColumnHeader.Click` 换成表头里「占用」那个 `TextBlock` 的
`MouseLeftButtonUp`。点了会同时改箭头方向与 ToolTip，用户才看得出当前是升还是降。

行选中态自己实现：点行清掉所有行的 `Background`，只给当前行铺强调色淡底。
点在行内的下拉框上时不抢事件 —— 用一个 `FindAncestor<ComboBox>` 判断。

警告横幅的两个写死色值 `#33E5534B` / `#E5534B` 换成 token：
`DangerSoft`（两册各自取值）与 `Danger`。`DangerSoft` 是新增的第 52 个键。

**设置页**

三卡重组：外观 / 行为 / 容错。原「启动与后台」六项拆成行为卡（四项）与容错卡（三项）。

强调色的 8 个预设圆点**整行删除**，换成 v12 的色相选择器：
36px 色块预览（带同色柔和外发光）+ 6px 彩虹渐变滑杆（0–360）+ 色值输入框。
这是用户明确要求的「完全按 v12，只留滑杆」。

新建 `Helpers/ColorHsl.cs`。WPF 的 `Color` 只有 RGB，没有 H 分量，
「当前颜色 → 滑杆位置」这一步必须自己算回来。用的公式是 CSS Color Module Level 3
的 HSL 定义，跟浏览器 `hsl()` 行为一致，色值能和用户在其他工具里看到的数字对上。
亮点是 `HueOf` 对灰阶返回 195 而不是 0 —— 强调色是灰的时候滑杆不该跳到最左。

滑杆拖动时的写盘做了区分：`HueSlider.IsMouseCaptureWithin` 为真说明用户还按着，
这一阶段只改画刷与预览，不落盘；松开后 `ValueChanged` 会再触发一次，这次才走
`ApplyCustomAccent` 存盘。否则拖一次滑杆就是几十次 `ui.json` 写入。

容错卡里「智能休眠感知」「软硬双重防砖保护」按 v12 显示为开启态但 `IsHitTestVisible="False"`
+ `Opacity 0.55`，ToolTip 写明为什么不能关。不假装成可切换的开关 ——
这两项目前是固定开启的保护逻辑，没有可关的入口。

「出错时通知我」是**真开关**：`UiSettingsStore` 新增 `NotifyOnError`（默认 true）。
它只控制要不要弹气泡，错误本身照样完整进日志。

「维护」卡是新加的（v12 没有）：SMU 自检与全局重置是排查问题的唯一入口，
删掉等于让用户失去自救手段。放在最后，视觉上压一级。

**日志页**

统计从底部一行「共 N 条」拆成顶部四枚 pill：调试 / 信息 / 警告 / 错误，
每枚带 6px 圆点 + 计数 + 级别色。pill 由代码生成（四份结构相同，XAML 写四份会让
「加一级」变成改五处）。

pill 的计数**不跟过滤走**。点一枚 WARN 筛选后，INFO 的计数仍是全量值 ——
pill 的作用是「告诉我总量里各有多少」，如果跟着过滤变成 0，用户会以为日志丢了。

pill 可点：点一下筛选，再点同一枚取消。用 `_levelFilter` 记当前筛选，
选中态是描边加粗 + 不降透明度，未选中降到 55%。

日志行从「整段按级别染色」改成「正文主色 + 只染左侧 3px 竖条和级别徽章」。
旧版 INFO 全是青色文字，一眼看过去满屏彩色反而找不到问题。

时间精确到毫秒（`HH:mm:ss.fff`），列宽固定 84 保证逐行对齐。

搜索框支持多关键词 AND 匹配，匹配范围是正文 + 级别名 + 时间。

「暂停」是真按钮：只冻结界面刷新，`LogService` 照常写文件。恢复时整体重建一次，
把暂停期间攒下的条目一次性补上。

「详细日志」开关保留（v12 没有）。这条过去只能靠启动参数或环境变量打开，
用户遇到问题得先知道去哪儿加参数，等于没有。

**对话框收口**

`MaskManagerWindow.TitleBar_MouseLeftButtonDown` 的 `DragMove()` 补上 `try/catch`。
`DragMove` 内部会 `SetCapture`，若鼠标在这之前已经松开，它抛
`InvalidOperationException`。UI 线程上未捕获的异常会直接把进程打掉 ——
拖不动是小事，闪退是大事。`DarkDialog` 那边早就有这个保护，这次对齐。

---

#### token 全量替换

两册 palette 从 51 键扩到 55 键，**差集为空**：

| 新增键 | Dark | Light | 用途 |
| --- | --- | --- | --- |
| `DangerSoft` | `#26E5534B` | `#1FE5534B` | 警告横幅淡底 |
| `Log.InfoTint` | `#1F60CDFF` | `#1F005FB8` | 日志 pill 底色 |
| `Log.WarnTint` | `#1FFFD60A` | `#1FC8A300` | 同上 |
| `Log.ErrorTint` | `#1FFF453A` | `#1FC42B1C` | 同上 |

调色板按 v12 全量替换：Window `#121214`、SideBar `#18181A`、
Card 3% 白、Card.Border 8% 白、Text.Secondary 60% 白、Text.Disabled 30% 白、
Input.Background 20% 黑、Scrollbar.Thumb 15% 白。

`Themes/Controls.xaml` 新增的组合样式：

| 样式 | 规格 |
| --- | --- |
| `ProcessRowStyle` | 透明底 + 圆角 6 + Padding 12,10 + hover 6% 白 |
| `TableHeaderTextStyle` | 12px 次要色 |
| `TableCellSubStyle` | 12px 次要色（路径 / 占用） |
| `TableCellMainStyle` | 13px 600 主色（进程名） |
| `HueSliderStyle` | 6px 彩虹轨道 + 18px 白圆 + 半透明黑描边 |
| `ColorPreviewStyle` | 36×36 圆角 6 |
| `ColorPickerGroupStyle` | 灰底 + 1px 边 + 24 内边距 |
| `LogItemBorderStyle` | 灰底 + 卡片边框 + 圆角 6 + Padding 12,10 |
| `LogTimeTextStyle` | 等宽 12px 30% 白 |
| `LogBadgeBorderStyle` | 圆角 4 + Padding 6,2 |
| `LogBadgeTextStyle` | 11px Bold |

`App.xaml` 的滚动条从 10px 收到 6px（横竖同步），与 v12 的 `::-webkit-scrollbar` 一致。

---

#### 验证

| 项 | 结果 |
| --- | --- |
| 编译 | **0 error / 0 warning** |
| 控件字段 | 61 → **66**（+15 新增 / −10 移除，逐项核对无悬空引用） |
| 事件处理器 | 55 → **60** |
| 两册 palette | 均 **55** 键，**差集为空** |
| `Accent.xaml` | **5** 键（代码契约未破） |
| 互操作红线 | `DllImport\|extern\|StructLayout\|FieldOffset` 在 `Views/` 下命中 **0** |
| 网页嵌套 | `WebView2\|WebBrowser\|WebView` 命中 **0** |

移除的 10 个控件名（`AccentPresetRow` / `FollowToggle` / `LogCountText` /
`ProcNameColumn` / `ProcPathColumn` / `ProcCpuColumn` / `ProcMaskColumn` /
`RuleNameColumn` / `RuleMaskColumn` / `RuleActionColumn`）在全部 `.cs` 与 `.xaml` 里
grep 结果均为 0 处引用。

---

### 0.10.0

界面重做，分三步走。这是第一步：窗口外壳与导航骨架。

**无边框外壳**

窗口改 `WindowStyle="None"`，`CaptionHeight` 从 48 降到 0。原来那条 48px 的自定义标题栏整个删掉，侧栏本身成了窗口外壳：顶部品牌、中部导航、底部版本号与窗口按钮，边缘到内容之间不再有任何一条「栏」。

`CaptionHeight` 必须为 0 不只是审美问题。只要它非零，系统就把这块区域当标题栏吃掉 `WM_NCLBUTTONDOWN`，挂在侧栏上的 `MouseLeftButtonDown` 根本不会触发，`DragMove` 变成死代码。

拖拽事件挂在侧栏最外层 `Border` 上，而不是只挂在 56px 的品牌区 —— 200px 宽的侧栏减去品牌文字只剩不到 100px，只在那一小块上拖等于逼用户瞄准。导航之外的每一寸，含导航下方整片空白，都能拖。

**三个新坑的处置**

- 最大化状态拖拽会把窗口「粘」在鼠标左上角跳一下。先还原，再按鼠标在窗口内的相对位置反推 `Left`。
- `WindowStyle="None"` 最大化会连任务栏一起盖住。用 `SystemParameters.WorkArea.Height - 2` 校正 `MaxHeight`，还原时放回无限。
- `ToggleButton` 吃掉 `MouseLeftButtonDown` 却不碰 `MouseLeftButtonUp`，于是「在侧栏空白处按下拖、在导航项上松手」会误跳页。改用类处理器 `AddHandler(..., handledEventsToo: true)` 把 Up 事件重新标记为 Handled —— 样式写在 `ResourceDictionary` 里挂不了实例处理器，编译直接报 `MC6024`。

**导航改为五项**

调校 / 进程 / 配置 一组，`系统` 分组标题下 设置 / 日志。选中态不再整块填充强调色，改成左侧 3×14 竖条 + 淡底 + 600 字重；整块填色会让侧栏变成一条彩色条，抢走内容区的注意力。

新增空的 `配置` 页（方案切换在第三步接入）。`ProcessBindingPage` 的导航标题由「调度」改为「进程」。

**新增 token**

`SideBar.Background` 与 `SideBar.Border` 两册同步。窗口底 `#121214`、侧栏 `#18181A` —— 两者只差 6/255，纯靠色差分不出边界，必须补那条 1px 分割线。

**新增样式**

`SideNavItemStyle`、`NavGroupTitleStyle`、`PageTitleStyle`、`PageSubStyle`。`WindowButtonStyle` 从 46×32 收到 28×28 圆角 4，因为窗口按钮挪进了侧栏底部。

**代价**

关闭按钮从右上挪到左下，违反 Windows 肌肉记忆（Alt+F4 与托盘菜单「退出」仍在）。拖拽改手写 `DragMove` 后完全失去 Aero Snap，拖到屏幕边缘不再吸附、拖到顶部不再自动最大化，只能靠 `Win+方向键`。这两条是无边框方案的固有取舍，不是疏漏。

### 0.9.6

修托盘。两处问题，都由 0.9.5 的实测反馈带出来。

**`NOTIFYICONDATA` 的字段偏移**

原定义用默认顺序布局，托管封送器认为 `Guid` 的对齐是 4 而不是 8，于是 `szInfoTitle` 落在 820，而 C 编译器按 shellapi.h 排出来是 824 —— 往后 `dwInfoFlags`、`guidItem` 也依次前移 4 字节。结构体还被撑到 976 字节，`cbSize` 报的也是 976。

只要 `uFlags` 不碰气泡通知那几位，这些字段根本不会被读，所以图标一直显示正常。但一旦要用 `NIF_INFO`，标题和状态位就会从错位的位置取，且整块长度与声明不一致。`Pack = 1` 救不了：它会把 `hWnd` 的对齐一起压掉，从第二个字段就错。

改为显式布局，15 个字段的偏移逐个钉死，并在类型加载时逐字段对拍 shellapi.h 的值，对不上就直接抛。`cbSize` 统一由 `sizeof` 与 `V3_SIZE` 常量算出，不再写死。

**托盘消息循环**

`RunMessageLoop` 原来用 `GetMessage` 收尾。`PostQuitMessage` 往队列塞的 `WM_QUIT` 被取到后只返回 0、并不出队，那条消息会一直躺着；等消息循环因唤醒而退出、之后又重入时，第一轮就会立刻退出，托盘图标随之消失。改成 `PeekMessage(PM_REMOVE)` 每轮把 `WM_QUIT` 真正摘掉。当前代码路径下不会重入，这属于修掉悬着的隐患。

**校验**

字段偏移、`SizeConst`（`szTip` 128 / `szInfo` 256 / `szInfoTitle` 64）、布局自检全部程序化核对；提交给 shell 的 968 字节与按 C 布局逐字段手工构造的参照块逐字节比对，968/968 一致。

### 0.9.5

本版为 0.9 系列重构的收口版本，没有新增功能、没有修复已知问题、没有改变任何业务行为。下面几节（`0.9.0-can1` 至 `0.9.5`）的改动合起来才构成这一版，六个中间包都可以丢弃。

底层接口对齐。这一轮碰的是与内核打交道的代码，原则是**只动调度与组织，不动任何对外契约**。

`CpuSetBinder.BindToMask` 的逐位探测改为走掩码里实际置位的位（`TrailingZeroCount` 依次消去最低位），并新增 `MaskWidth` 把超出本机核数的位一次性截掉。语义与旧实现完全相同 —— 12 组典型掩码（含全 1、仅最高位、32 位边界）× 10 种核数（1 / 4 / 8 / 16 / 24 / 32 / 63 / 64 / 96 / 128）加 3000 组随机对照，3120 个用例的 CpuSetId 序列逐条一致。

`NativeTrayIcon`：`NOTIFYICONDATA` 的构造在两处（添加 / 删除图标）重复了一遍，收敛成 `NewIconData()`。字段赋值顺序与取值不变，`cbSize` 仍按 `Marshal.SizeOf` 现算。

`ProcessWatcher`：补上两处并发意图说明 —— `HandleProcess` 里的 `Contains` 前置判断不是 `TryAdd` 的冗余（它省掉的是规则表的整表遍历），`ScanOnce` 的清理门槛用「记录数 > 存活数」而不是固定阈值。

红线部分逐字未动，已用 diff 核对：`WakeWindow` / `NativeTrayIcon` / `CpuSetBinder` / `ProcessRefresher` 里的全部 `[DllImport]` 声明、`SYSTEM_CPU_SET_INFORMATION` / `WNDCLASSEX` / `NOTIFYICONDATA` / `PROCESSENTRY32` / `MSG` 等结构体的字段与 `[StructLayout]` 参数，一行没改。

### 0.9.2-can1

模型与接口扫尾，不改动任何业务逻辑。

四个配置 store（`ui.json` / `bindings.json` / `masks.json` / `tuning.json`）原本各有一份一模一样的「先写 .tmp 再原子替换」代码，统一收进新的 `Helpers/JsonFile.cs`；四个路径常量也从「先拼目录再拼文件名」合并成一次 `Path.Combine`，目录创建交给写入方统一负责。新旧实现的写入结果逐字节比对一致（缩进格式、换行、`.tmp` 是否残留、缺失文件与内容为 `null` 的返回值全部核对过）。

删掉三个确认无调用方的公开成员：`SmuReader.IsSharedAvailable`（已有等价的 `PeekCoresPerCcdRaw`）、`MaskFormatter.ToHexText`、`SilentRunResult.AnythingApplied`（退出码只用 `Errors` 与 `HasActiveRules` 判定）。`SmuReader` 里一处指向自身命名空间的冗余 `using`、以及几处随重构失去用途的命名空间引用一并清掉。

接口面保留未删的成员：`ThemeService.ApplyFromSettings`（`Initialize` 的唯一调用点）、`SmuReader.TryCreate`、`ProcessWatcher` 内部的 `TryAdd` / `PruneTo` —— 它们都有实际调用者，只是同名符号的检索会误报。

### 0.9.1-can2

第二轮界面复核，仍是纯整理。

`SettingsPage`：九处「屏蔽回调 → 改控件 → 恢复回调」的重复样板收敛成 `Suppressed` / `SetToggle` 两个辅助方法，`LoadSettings` 与 `SetBackendDependentToggles` 因此缩成表达式体。

`ProcessBindingPage`：三处重复的目标名称推导收敛成 `TargetLabel`；`ApplySavedNow` 改为复用 `BindGroupMembers`，原先它是同一段下发循环的第二份拷贝（日志格式随之统一）。

`TuningPage`：`ApplyAllButton_Click` 与 `SaveConfigButton_Click` 共用 `TryCollectInputs` 收集校验；只读探测与手动刷新的失败处理收敛成 `TryUseSnapshot`；删掉一段关于已删除控件的 TODO。

`MaskManagerWindow`：可视树查找的递归实现收敛成单层循环。

`App` / `MainWindow` / `UiSettingsStore` / `UiPresence`：压缩一批解释性注释的篇幅，删掉已经与代码不符的分支描述（例如「两个兜底」实际只剩一条），保留全部「为什么」而不重复代码字面意思。`MainWindow.UpdateUiPresence` 的文档注释此前是半句话，一并补完整。

### 0.9.1-can1

界面层整理，不改动任何业务逻辑与执行流程。

`SettingsPage`：三处「抑制回调 → 改值 → 恢复回调」的重复块收敛成 `SyncAccentUi`，后端的关闭 / 重开提成 `CloseBackend` / `ReopenBackend`，全局恢复默认里 60 行深嵌套的 SMU 写序列提成 `WriteFactoryToSmuAsync`，十六进制判定的逐字符循环改用 `Uri.IsHexDigit`。

`ProcessBindingPage`：两处重复的 switch 绑定收敛成 `BindMember`，「改完规则就重载缓存」的四次重复组合收敛成 `ReloadRulesAfterChange`，绑定快照的手写累加循环改用 `Count` / `Sum` / `Select`。

`MaskManagerWindow`：两处重复的编辑器复位收敛成 `ResetEditor`，重名建议的 `while` 试探改用 `Enumerable.Range`。

`MainWindow`：关闭事件的三个分支合并，`e.Cancel = true; Hide();` 提到分支外只留一处；倒计时 Tick 的 lambda 改为具名方法。

`App`：`HardStop` 里五段重复的 try/catch 收敛成 `Quietly`，启动流程里重复的唤醒通道挂接与 store 加载各自提成独立方法，命令行拼接改用 LINQ。

`TuningPage`：三处「探测 SMU 快照」的重复逻辑收敛成 `ReadSnapshotAsync`，异步方法改用元组返回以避开 `out` 参数限制。

`DarkDialog`：图标配色表提成 `IconPalette`，上色逻辑从 `Create` 里拆出为 `ApplyIcon`；`LogPage`：跟随定时器的三处重复停止判断收敛成 `StopFollow`。

顺带修正 `App` 里一个错位的文档注释 —— 描述「真正的主窗口」的那段 `<summary>` 此前挂在了 `VersionText` 上，导致该属性有两个连续的说明块。

### 0.9.0-can2

第二轮复核，仍是纯整理，不改动任何业务逻辑与执行流程。

`Helpers`：`UiSettingsStore` 的读盘分支合并为一条路径（旧配置兼容判定不变），`Directory` 字段改名，不再需要用全名绕开 `System.IO.Directory`；`MaskFormatter` 把三层嵌套的解析拆成「判十六进制 / 解析十六进制 / 解析区间与枚举」三个方法，删掉两个没有调用方的输出方法，42 组输入的新旧解析结果逐条比对一致；`MaskNamePolicy` 删掉没有调用方的计数文本方法。

`Services`：`CpuSetBinder` 把六处重复的结果构造收敛成两个工厂，拓扑加载的主体拆成独立方法，缓冲区读取与条目解析各自独立；`ProcessRefresher` 的采样循环拆成三个方法，顺带修正 64 位下无效句柄的判定（原先按 32 位常量比较，永远比不中真实的 `INVALID_HANDLE_VALUE`）；`LogService` 抽出带重试的删除与日志文件名日期解析；`SmuReader` 去掉一处重复的版本号格式化；`ProcessWatcher` 的规则匹配改为单一谓词。

死代码处置说明：`WakeWindow` / `NativeTrayIcon` / `CpuSetBinder` 里那几个「有 API 语义但当前无调用方」的成员（如 `IsCreated`、`ExitRequested`、`ResetTopology`）保留未删 —— 它们属于进程控制与系统拓扑的对外接口面，删了在后续改造里反而要补回来。

### 0.9.0-can1

第一轮代码整理，不改动任何业务逻辑与执行流程。

`Helpers`：`BindingStore` 的 Load / Reload 合并为同一条读盘实现，两者此前是逐字重复的两份；`MaskStore` 的核数统计改用 `BitOperations.PopCount`，删掉没有调用方的单参 `Upsert` 重载；`MaskFormatter` 的最高位计算改用 `BitOperations.Log2`；`ThemeService` 把主题轮询里纠缠在一起的两个分支拆成各自的方法；`WindowHelper` 把四处重复的句柄获取收敛成一处，常量与 P/Invoke 声明归拢到类首。

`Services`：`WakeWindow` 与 `SingleInstance` 只精简注释，P/Invoke 结构体字段一字未动；`HeadlessHost` 把「托盘建不出来」的降级等待提成独立方法；`TuningApplyService` 去掉空转的 try/finally（异常传播行为不变）与重复的跳过结果构造；`TrayIconService` 删掉两个没有任何调用方的托盘方法及其字段。

另：清理全项目残留的开发阶段标记（S1 / S2 / S3 等，现在已无对应含义）与纯装饰性分隔注释，删掉一批只是复述代码字面意思的注释。

### 0.8.6

UI 重构前的最后一轮文案与交互修正。

「仅留服务进程」的延迟从 30 秒改为 3 秒。这个延迟只是给「刚点 × 就想反悔」留一个回头的窗口，不是给系统留出准备时间；等半分钟才销毁界面，用户早就忘了自己开着它。

调校页顶部按钮改为四个：保存、加载、刷新、应用。原先的「读取」改叫「加载」，因为它读的是本地 `tuning.json`，叫「读取」会让人以为是读硬件现值；新增「刷新」负责从 SMU 现读当前生效值回填界面，只读不写。四个动作方向严格区分：保存是界面到存档，加载是存档到界面，刷新是硬件到界面，应用是界面到硬件并落盘。

「全部应用」改名为「应用」，成为唯一的写入口。删除 Curve Optimizer 卡片下的「应用」「恢复初始值」，以及 FMax 卡片下的「应用」—— 分开写会出现「CO 生效了但 FMax 还是旧的」这种半套状态。需要把某个区块恢复默认时，直接从硬件刷新即可。

删除 Curve Optimizer 里「分核负压」这行标题。全核负压输入框在 0.8.3 已移除，「分核」失去了与「全核」对照的意义，留着只会让人找一个不存在的选项。

### 0.8.5

设置页「启动与后台」收敛为六行平铺：开机静默自启、自动应用调校、开启后台服务、后台常驻调度、仅留服务进程、显示托盘图标。删除了项间的分隔线与说明副标题，取消所有子级缩进 —— 之前子菜单与非子菜单混排，同一列里三种层级样式并存。

「仅留服务进程」合并了原先的「后台轻量模式」与「轻量模式行为」两项：点 × 后隐藏，稍后销毁前台界面，进程只剩后台服务（延迟在 0.8.6 改为 3 秒）。「仅清空页面」已删除 —— 实测两条路径工作集都被压到个位数 MB，差别落在测量噪声里，留两个只增加理解成本。延迟暂不可调，等换 UI 后再考虑加回输入框。

清空日志不再卡死。原因有三：清空时在 UI 线程上递归 `Post` 自己；`File.Delete` 与写盘线程的 `AppendAllText` 抢占同一个文件句柄（默认只给 `Read` 共享，删不掉又反复失败）；`ObservableCollection.Clear()` 在被绑定的 `ListBox` 上触发整列表容器回收，同时队列里还压着半拍 `ScrollIntoView`。现在内存清空与磁盘删除彻底分离，删除走后台并短重试，写盘改用 `FileShare.ReadWrite | FileShare.Delete`，日志增加「代号」让清空前取走的批次自动作废，界面侧先摘掉 `ItemsSource` 再清、清完挂回。

单实例唤醒改用隐藏消息窗 + `RegisterWindowMessage` / `PostMessage`，并放行 UIPI 过滤。计划任务以最高权限常驻时，普通权限双击 exe 也能唤出已有界面，不再另起进程。

进入轻量模式时执行内存回收：压缩大对象堆、强制回收，并把工作集交还系统。同时修复主窗口对主题静态事件的可回收性缺陷 —— 此前窗口关闭后页面可视树仍被事件表握住，无论选哪种轻量路径工作集都不会降。

移除调校页的功耗墙卡片（PPT / TDC / EDC / 温度墙）及相关写入、基线采集与存档。该平台缺少可靠通道，保留只会给出误导性的可调假象。

修复设置页打不开、启动时弹 `XamlParseException` 的问题。根因是行样式里为 `Grid.ColumnDefinitions` 写了 Setter —— 该属性是只读集合，BAML 解析出的 Property 为 null，导致整个资源字典失效。资源字典遍历另加容错，单个样式坏掉不再拖垮启动。

### 0.8.3

移除调校页的全核负压输入框与「填充到全核」按钮。逐核负压输入与两个按钮保留不变。

功耗墙卡片移至 FMax 卡片上方，打开调校页即可见。

### 0.8.2

新增功耗墙调校，位于调校页：PPT 功耗上限、TDC 持续电流、EDC 峰值电流、Tctl 温度上限。

启动时采集出厂基线并保存至 `power-baseline.json`，恢复默认读取该文件而非现读。另设「现读并覆盖」，用于换硬件或刷 BIOS 后重新采集。

存档与现读不一致时记录警告日志，存档值保持不变。

四项逐项独立写入，单项失败不影响其它项。失败按「该平台不支持」「需在 BIOS 开启 PBO」「瞬时故障」三类分别提示，前置条件不满足时界面标红。

EDC 无出厂融断通道，仅允许写入，不参与恢复。

温度墙回读允许 ±2 ℃ 偏差，因出厂通道与写入通道不同源。

配置保存新增四项墙值，开机静默自启一并应用。

### 0.8.1

修复后台宿主升级为图形界面时必然崩溃的问题，同步移除三处 `StartupUri` 赋 null 的写法。

修复关闭托盘图标后关闭窗口即退出进程的问题。关窗行为现仅由「关闭窗口时」设置决定，托盘状态不再参与判定。

升级失败不再终止进程，改为回退至托盘常驻，进程绑核继续运行。

升级分支补充全局异常处理，并新增进程级未处理异常兜底记录。

升级流程拆分为六个阶段并输出日志，用于定位中断位置。

设置页托盘开关的说明文字改为与实际行为一致的描述。

### 0.8.0

进程入口按运行方式分流，静默自启的进程不再加载 WPF。后台路径不引用 PresentationFramework、PresentationCore、WindowsBase 与 System.Windows.Forms。

后台托盘图标改为纯 Win32 实现，不依赖 WinForms 与 WPF。

常驻判据改为四项同时成立：自启、绑核开关开启、存在生效规则、监听归本进程。

设置页新增「后台显示托盘图标」开关。

日志集合增加锁保护，避免无界面时 WMI 线程与轮询线程并发写入。

### 0.7.12

应用清单改回 `requireAdministrator`，双击即提权，移除运行时以 `runas` 重启自身的提权链路。

「清空日志」改为同时清除内存集合、未落盘队列与磁盘日志文件；启动时自动删除 14 天前的日志。

### 0.7.11

日志列表启用虚拟化，`CanContentScroll` 设为 `True`；超限裁剪改为每批 256 条，清空前停止跟随定时器。

界面启用 `UseLayoutRounding`，并在 `ContentRendered` 与从托盘恢复时各强制重绘一次。

新增 `ReleaseForTray` 与 `RestoreFromTray`：清空导航堆栈、拆解页面可视树、停止主题轮询，恢复时返回上次所在页面。

### 0.7.10

关闭窗口时，若存在后台职责则收进托盘，否则退出。

日志写入由同步改为后台每 500ms 批量落盘；日志集合上限设为 2000 条。

日志页的滚动跟随改为 200ms 合并执行。

焦点框定位由 400ms 定时器遍历可视树改为键盘焦点事件。

### 0.7.8

回退 0.7.7 的 `SizeToContent="Height"`，窗口恢复固定尺寸。

列宽测量改用 `FormattedText`，按本窗口的字体、字号与 DPI 计算，不再依赖未挂入可视树的控件。

移除为自适应高度服务的 `MaxHeight` 封顶逻辑。

### 0.7.7

默认尺寸由 820×620 收敛为 730 宽、高度随内容。

勾选区由固定 4 列改为按可用宽度计算列数并均分宽度，列数上限 5。

列表行高由 46 调整为 40。

### 0.7.6

掩码窗口左列改为垂直拉伸至底边。

列表行高由 30 调整为 46；删除按钮由常驻槽位改为悬停浮层，左列宽由 208 收至 168。

窗口默认尺寸由 880×660 调整为 820×620。

### 0.7.5

自定义掩码上限设为 10 套，达到上限后「新建」置灰，标题右侧显示计数，存储层同步拦截。

禁止重名，比较忽略大小写；改名、保存、新建共用同一套校验，写入前再校验一次；建议名称跳过已占用项。

掩码窗口支持缩放，勾选区按可用宽度自动折行。

日志页新增「详细日志」开关，切换后立即生效，不持久化。

### 0.7.4

「新建」改为仅在内存中创建草稿，点「保存」后落盘。

删除图标由 Segoe MDL2 字形改为矢量路径绘制。

列表行高固定，行内输入框高度固定为 24px。

改名交互调整：回车确认，失焦静默确认（不合法则回退原名），Esc、点击空白、取消、关窗均放弃改名。

名称输入超限时实时裁剪。

### 0.7.3

`SmuReader.Dispose()` 改为 no-op，`TryCreate` 失败时不再释放实例，`Shared` 不再重建实例，`TuningApplyService` 移除 `finally` 中的 Dispose。

原因：`Cpu.Dispose()` 会关闭进程级共享的静态 Opcode 资源，导致此后全部实例无法执行 CPUID 与 SMU 命令。

### 0.7.2

单例创建失败不再永久缓存，改为 2 秒冷却后重建。

`WaitForPawnIO` 复用单例，不再另建实例；拓扑推断改用 `PeekCoresPerCcdRaw()`。

`TryCreate` 增加 SMU 测试消息校验。

### 0.7.1

WMI 事件查询的 SELECT 由带点号的属性路径改为 `SELECT *`，`WITHIN` 由 1 调整为 2。

Curve Optimizer 的核心掩码改为调用库的 `SetPsmMarginSingleCore(core, ccd, ccx, margin)` 三参重载，不再自行计算。

FMax 写入前先读取基线，基线为 0 时不再判定为写入失败。

### 0.7.0

进程绑定监听由每 1 秒枚举进程表改为订阅系统进程创建与删除事件。

WMI 不可用时弹窗提示并降级为 3 秒轮询；静默自启时仅记录日志，不弹窗。

进程列表采样改为按需执行：调度页可见且未最小化时每 3 秒采样，其余情况挂起。

修复进程快照的陈旧样本清理条件。

绑定规则读写加锁。

修复日志清空时的跨线程调用异常。

修复调用 `schtasks` 时 stdout 与 stderr 顺序读取导致的管道死锁，改为并发读取并加 15 秒超时。
