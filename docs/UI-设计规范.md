# ZenForge UI 设计规范与实现标准

> 基线：`1.0.0-can10` · WPF / .NET 10 · Windows 桌面
> 本文档的所有数值、键名均从代码中逐条核对得出，不是设计理想值。
> **引用键名，不要发明新值。** 文末「已知漂移」列出了代码里存在但不该再用的东西。

---

## 0. 这份文档怎么用

给任何要改 ZenForge 界面的人（或 AI）的三条前置规则：

1. **颜色一律引用资源键**，例如 `{DynamicResource Text.Secondary}`。禁止写 `Foreground="#999999"`。
2. **字号一律用 `Text.*` 样式**，禁止在页面里写 `FontSize="14"`。全工程只有 7 个字号（§3.2）。
3. **尺寸优先用既有样式**（`CardBorderStyle` / `SecondaryButtonStyle` / `ProcessRowStyle` …）。样式没覆盖时才写裸值，写之前先确认它不是某个既有约定的一部分。

违反这三条不会编译报错，但会在下一次主题切换、缩放变化或高 DPI 下暴露成「花斑 / 高低不齐 / 文字发虚」。

---

## 1. 设计基准

| 项 | 值 |
|---|---|
| 主窗口默认尺寸 | 1120 × 780 |
| 主窗口最小尺寸 | 920 × 600 |
| 设计目标分辨率 | 1920 × 1080 @ 100% 缩放 |
| 缩放适配要求 | 125% / 150% 下不得出现文字截断、控件重叠 |
| 主题 | 深色（默认）/ 浅色，可跟随系统 |
| 强调色 | 默认 `#38BDF8`，可跟随系统或用户自定 |

**缩放相关硬要求**：所有文字使用 `TextOptions.TextFormattingMode="Display"`（按整数像素对齐字形）。`Ideal` 模式下 13px 中文字在 100% 缩放下会发虚、边缘发灰。这一项已经写在全局隐式 `TextBlock` 样式里，页面不需要重复设置，但也**不要**用 `Ideal` 覆盖它。

---

## 2. 颜色系统

### 2.1 三层结构

```
Themes/Accent.xaml          5 个键    运行时可被 ThemeService 改写
Themes/Palette.Dark.xaml    ~50 个键   深色册
Themes/Palette.Light.xaml   ~50 个键   浅色册（键名与深色册完全一致，整册替换）
```

- 两套色板**键名必须完全对齐**。`ThemeService.SwapPalette` 是按 URI 整册替换的，缺一个键就是运行期 `null` → 控件透明或文字消失。
- 代码里新增颜色 = 两册都要加，一次加两个。

### 2.2 完整键表

#### 结构层

| 键 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `Window.Background` | `#121214` | `#F3F3F4` | 窗口底板 |
| `SideBar.Background` | `#18181A` | `#EBEBED` | 侧栏。深色比窗口**亮**一档，浅色比窗口**暗**一档，方向相反、目的相同：亮出边界 |
| `SideBar.Border` | `#14FFFFFF` | `#10000000` | 侧栏分割线（1px） |
| `TitleBar.Background` | `#121214` | `#F3F3F4` | 标题栏。**必须不透明**，透明会在硬件渲染失效时 "破洞" |
| `Nav.Background` | `#18181A` | `#EBEBED` | 导航区底色 |
| `Card.Background` | `#08FFFFFF` | `#F2FFFFFF` | 卡片底色（半透明，叠在窗口上） |
| `Card.Border` | `#14FFFFFF` | `#14000000` | 卡片 1px 边框 |
| `Separator` | `#14FFFFFF` | `#14000000` | 通用分割线 |

#### 文字层（四级，**严禁全白铺满**）

| 键 | 深色 | 浅色 | 用途 | 对应字号 |
|---|---|---|---|---|
| `Text.Primary` | `#FFFFFF` | `#E4000000` | 标题、主标签、数值 | L1 / L2 / L3 / L4 |
| `Text.Secondary` | `#9AFFFFFF`（60%） | `#99000000`（60%） | 说明文字 | L5 |
| `Text.MetaBrush` | `#75FFFFFF`（46%） | `#7A000000`（46%） | 元数据：路径、时间、版本、计数 | L6 / L7 |
| `Text.Disabled` | `#4DFFFFFF`（30%） | `#4D000000`（30%） | **仅**表示「不可用」 | — |

> `Text.Disabled` 与 `Text.MetaBrush` 语义不同，**不可互换**。历史上两者混用，导致元数据对比度只有 2.6:1，1080p 下几乎读不出来。

#### 控件层

| 键 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `Control.Background` | `#0FFFFFFF` | `#FCFFFFFF` | 输入框 / 下拉框静态底。「可交互 = 浮起」：深色下比卡片亮，浅色下比卡片白 |
| `Control.BackgroundHover` | `#17FFFFFF` | `#FFFFFFFF` | 悬停底 |
| `Input.Background` | `#33000000` | `#0A000000` | 内嵌输入区（如表格内） |
| `Input.Foreground` | `#FFFFFF` | `#1C1C1E` | 输入文字 |
| `Input.Border` | `#1AFFFFFF` | `#14000000` | 输入框 1px 边框 |
| `Input.BorderHover` | `#40FFFFFF` | `#33000000` | 悬停边框 |
| `Nav.Button.Hover` | `#0FFFFFFF` | `#0D000000` | 导航项悬停 |
| `Nav.Button.Active` | `#14FFFFFF` | `#14000000` | 导航项选中底 |
| `Toggle.Off` | `#1AFFFFFF` | `#1F000000` | 开关关闭态轨道 |
| `Toggle.Thumb` | `#FFFFFF` | `#FFFFFF` | 开关滑块 |
| `Toggle.ThumbBorder` | `Transparent` | `#8A8A90` | 开关滑块描边（浅色下必须有一圈，否则开关像空的） |
| `Disabled.Background` | `#2A2A2C` | `#F0F0F2` | 禁用底 |
| `Disabled.Foreground` | `#6C6C70` | `#A0A0A5` | 禁用文字 |

#### 列表 / 滚动 / 弹层

| 键 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `List.Background` | `#252526` | `#FFFFFF` | 列表底 |
| `List.Header` | `#1E1E1E` | `#F2F2F4` | 表头底 |
| `List.Hover` | `#0FFFFFFF` | `#0D000000` | 行悬停 |
| `List.SelectedText` | `#101010` | `#101010` | 选中行文字（压在强调色上） |
| `Row.FadeBrush` | `#00→#FF 252526` | `#00→#FF FFFFFF` | 行内删除按钮的左侧渐隐衬底，左 10px 过渡 |
| `Scrollbar.Thumb` | `#26FFFFFF` | `#26000000` | 滚动条滑块 |
| `Scrollbar.ThumbHover` | `#4DFFFFFF` | `#4D000000` | 滑块悬停 |
| `Menu.Background` | `#1F1F21` | `#FBFBFC` | 弹出菜单 / 右键菜单。**必须实色**，浮在桌面上没有窗口垫底 |
| `Menu.Border` | `#29FFFFFF` | `#1A000000` | 菜单边框 |
| `ToolTip.Background` | `#2D2D30` | `#FFFFFF` | 提示框 |
| `ToolTip.Foreground` | `#F0F0F0` | `#1C1C1E` | 提示文字 |
| `ToolTip.Border` | `#3F3F46` | `#CFCFD4` | 提示框边框 |
| `Toast.Background` | `#1E1E20` | `#FFFFFF` | Toast 卡片 |
| `Toast.Border` | `#1FFFFFFF` | `#14000000` | Toast 边框 |
| `Badge.Background` | `#14FFFFFF` | `#14000000` | 状态徽章底。必须比所在卡片**亮**一档，否则像挖了个洞 |

#### 按钮（中性色，不跟随强调色）

| 键 | 深色 | 浅色 |
|---|---|---|
| `Button.Neutral.Background` | `#2A2A2D` | `#F7F7F9` |
| `Button.Neutral.Hover` | `#38383D` | `#EDEDF0` |
| `Button.Neutral.Pressed` | `#212124` | `#E2E2E7` |
| `Button.Neutral.Foreground` | `#F0F0F0` | `#1C1C1E` |
| `Button.Neutral.Border` | `#33FFFFFF` | `#C9C9D0` |
| `Button.Quiet.Background` | `#1C1C1F` | `#F7F7F9` |

> 次要按钮的定位是「看得见、点得到、不抢视线」——靠 1px 边框和 hover 提亮表达可交互，不靠底色发亮。一排四个发亮的灰方块会把主内容（CPU 名）压下去。

#### 日志语义色（**铁律区**）

| 键 | 深色 | 浅色 |
|---|---|---|
| `Log.Info` | `#8B9BB4` | `#4A5A70` |
| `Log.Warn` | `#F5D90A` | `#B26A00` |
| `Log.Error` | `#FF453A` | `#D13A2F` |
| `Log.Critical` | `#C42B1C` | `#8F1E13` |
| `Log.InfoTint` | `#1F8B9BB4` | `#1F4A5A70` |
| `Log.WarnTint` | `#1FF5D90A` | `#1FB26A00` |
| `Log.ErrorTint` | `#1FFF453A` | `#1FD13A2F` |
| `Log.CriticalTint` | `#1FC42B1C` | `#1F8F1E13` |

> **这组颜色禁止与强调色产生任何关联。** 用户把强调色改成紫色、橙色、绿色，警告必须还是黄的、错误必须还是红的——否则「红色」不再意味着错误，界面语言就失效了。`ThemeService.SetAccentBrushes` 有白名单校验，只允许写 `Accent.xaml` 里的 5 个键。
> `*Tint` 是对应级别色的 12%（`0x1F`）透明度版本，用于徽章底。改一个必须改另一个。

#### 状态色（通用，非日志）

| 键 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `State.Info` | `#B8B8BC` | `#5A5A5E` | 中性状态 |
| `State.Success` | `#2FB344` | `#1F8A34` | 成功 |
| `State.Warning` | `#E8A33D` | `#B26A00` | 警告 |
| `State.Danger` | `#E86A6A` | `#C42B1C` | 危险 |
| `Danger` | `#C42B1C` | `#C42B1C` | 危险操作主色 |
| `DangerSoft` | `#26E5534B` | `#1FE5534B` | 警告横幅淡底 |

#### 杂项

| 键 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `CloseButton.Foreground` | `#FFFFFF` | `#FFFFFF` | 关闭按钮符号 |
| `CloseButton.Pressed` | `#A32218` | `#C42B1C` | 关闭按钮按下 |

#### 强调色（运行时改写）

| 键 | 默认值 | 用途 |
|---|---|---|
| `AccentBrush` | `#38BDF8` | 主强调：选中态、聚焦边框、滑块填充、下拉展开边框 |
| `AccentHoverBrush` | `#5CC9FA` | 强调色悬停 |
| `AccentPressedBrush` | `#2AA0D4` | 强调色按下 |
| `AccentSoftBrush` | `#2638BDF8` | 强调色 15% 底（选中行底、浅色高亮块） |
| `AccentTextBrush` | `#082F49` | 压在强调色上的文字 |

### 2.3 颜色使用纪律

1. **严禁全白铺满**。主体 100% / 说明 60% / 元数据 46% / 不可用 30%。每级都用纯白，字号拉得再开也读不出主次。
2. **一个语义只有一个颜色**。同一含义不允许在界面上出现第二种灰或第二种黄。
3. **语义色硬编码**，不引用 `Accent*`。
4. **深色/浅色是两套独立取值**，不是同一颜色改透明度。浅底上不能用深色册的亮黄，反过来也一样。
5. 新增颜色键必须同时进两册，并在两册写注释说明为什么需要它。

---

## 3. 字体系统

### 3.1 字体栈

| 键 | 值 | 用途 |
|---|---|---|
| `AppFontFamily` | `Microsoft YaHei UI, Microsoft YaHei, Segoe UI` | 全部界面文字 |
| `MonoFontFamily` | `Consolas, Cascadia Mono, Courier New, monospace` | 数值、时间戳、掩码位串、版本号 |
| `Segoe MDL2 Assets` | 系统字体 | 少量图标字形（窗口按钮 `&#xE921;` 等） |

等宽是**必须**的：`-28` 与 `0`、`4750` 与 `475` 在比例字体下数位宽度不同，一列数字会参差不齐。

### 3.2 字号阶梯（7 个字号 / 9 个样式键，唯一口径）

| 级 | 样式键 | 字号 | 字重 | 颜色 | 用途 |
|---|---|---|---|---|---|
| L1 | `Text.PageTitle` | **26** | Bold | Primary | 页面大标题。每页唯一一行，下边距 20 |
| L1-副 | `Text.PageSub` | **11** | Normal | Meta | 页标题下的副行，上边距 6 |
| L2 | `Text.CardTitle` | **15** | SemiBold | Primary | 卡片标题。每卡唯一一行，下边距 14 |
| L3 | `Text.Numeric` | **16** | Bold | Primary | 核心数值（等宽）：CO 每格、FMax、百分比 |
| L4 | `Text.Label` | **13** | Medium | Primary | 主标签、正文、表格主列 |
| L5 | `Text.Desc` | **12** | Normal | Secondary | 说明、注释。上边距 4 |
| L6 | `Text.Meta` | **11** | Normal | Meta | 路径、时间戳、版本、计数 |
| L6-mono | `Text.MetaMono` | 11 | Normal | Meta | 元数据但需等宽 |
| L7 | `Text.Micro` | **10** | SemiBold | Meta | 全大写微标签：分组名、表头 |

**级差原则**：层级靠「级差」而不是「都够大」。26 / 15 / 13 / 12 / 11 / 10 的相邻差值都是刻意选的。

- L2 取 15 而不是 14：15 与 L4 的 13 差 2px，配 SemiBold + 纯白才能读成「标题」；14 只差 1px，加粗也救不回来。
- 不要新增字号。现有 7 个字号不够用时，先问「是不是用错了级」。
- `Text.PageSub` 是 L1 的副行（11px），`Text.MetaMono` 是 L6 的等宽变体，两者不算新增级别。

### 3.3 行距与组合块

| 场景 | 值 | 说明 |
|---|---|---|
| 卡片标题 → 第一行内容 | 14 | 断开但不空到读成两个模块 |
| L4 标签 → 下方 L5 说明 | 4 | 「同一组」的最小间距。再小黏成灰块，再大变成两个条目 |
| 页标题 → 内容区 | 20 | 卡片自身已有 20 内边距，24 会让第一张卡「掉下去」 |
| 「标签 + 说明」两行块 | `Stack.LabelBlock` | Vertical + `VerticalAlignment=Center`。**必须 Center**，否则与右侧开关/下拉的中线不重合 |

`TextBlock` 没有 `letter-spacing`。**不做伪字距**——给每个中文之间插空格会让它读成断句。

---

## 4. 间距与尺寸

### 4.1 间距刻度（代码实际用值）

```
1  2  4  6  8  10  12  14  16  18  20  22  24  26  28
```

以 4 的倍数为主；2 / 6 / 10 / 14 / 18 / 22 / 26 用于像素级微调（多为「奇数宽度的控件居中」）。**不要引入 5 / 7 / 9 / 15 这类值。**

### 4.2 关键间距口径

| 位置 | 值 |
|---|---|
| 页面外边距 | `28, 6, 28, 24` |
| 卡片之间 | 16 |
| 卡片内边距 | `20, 22` |
| 卡片圆角 | 10 |
| 设置项行距 `SettingsRowStyle` | 上边距 20 |
| 从属行 `SettingsSubRowStyle` | `16, 10, 0, 0`（左侧缩进 16 + 上 10） |
| 分组分隔线 | 高 1，`0, 14, 0, 2` |
| 卡片标题区 `CardHeaderBorderStyle` | 内距下 16 + 外边距下 16 |
| 卡片内行 `CardRowStyle` | 下边距 20 |

> 设置页行距为什么是 20 不是 24：每行实际内容约 34px 高，行距 24 时相邻两行间隔比行本身还高，一屏只能放四行，设置页变成散点图。组与组之间的大间隔由分隔线承担。

### 4.3 控件高度梯（**同屏并排必须等高**）

| 高度 | 用途 | 样式键 |
|---|---|---|
| 20 | 开关 | `ToggleSwitchStyle`（36×20） |
| 24 | 滑块（通用 `Slider` 隐式样式） | `HueSliderStyle` 同为 24；核心停放滑块页面覆盖为 32 |
| 26 | 日志级别 pill | `LogPillStyle`（Padding `12,0`，圆角 13） |
| 28 | 页头小按钮、图标按钮、状态徽章 | `HeaderActionButtonStyle`（Padding `14,0`，字号 12）、`IconButtonStyle`（28×28）、`StatusBadgeStyle`（Padding `14,0`，圆角 6） |
| 30 | 菜单项 / 下拉项 | `AppComboBoxItemStyle`（MinHeight 30，Padding `10,7`）、`TitleBarButtonStyle` |
| 32 | **标准控件高度**：输入框、下拉框、次要按钮 | `AppTextBoxStyle`、`AppComboBoxStyle`、`SecondaryButtonStyle`（Padding `14,0`） |
| 34 | 顶部导航按钮 | `NavButtonStyle`（Padding `16,0`） |
| 36 | 侧栏导航项 | `SideNavItemStyle`（圆角 8，下边距 2） |
| 36 | 色块预览 | `ColorPreviewStyle`（36×36，圆角 6） |
| 40 | 标题栏拖拽条 | 布局行高 |
| 52 | 侧栏品牌行 | 布局行高 |
| 76 | 页头卡片 | `PageHeaderCardStyle`（**当前未被使用**，见 §10） |

**32 是全工程标准控件高度。** 除上表已登记的例外，任何地方都不该再写 `Height`。历史上出现过 36 / 32 / 30 三种输入框高度并排，同一屏高低不齐。

### 4.4 圆角梯

| 圆角 | 用途 |
|---|---|
| 2 | 通用滑块轨道（高 4） |
| 3 | 滚动条滑块、色相滑杆轨道（高 6） |
| 4 | 小图标按钮、菜单项高亮块、下拉项 |
| 6 | **标准**：输入框、下拉框、按钮、表格行、色块、配置项 |
| 8 | 侧栏导航项 |
| 10 | 卡片、开关轨道（高 20） |
| 12 | 对话框（含标题栏 `12,12,0,0`） |
| 13 | pill 徽章（高 26 / 2） |
| 17 | 圆形按钮（高 34 / 2） |

### 4.5 布局骨架

```
Window 1120×780（min 920×600）
├── 侧栏 208
│   ├── 品牌行 52
│   ├── 导航区 *（滚动）      项高 36，下边距 2，内边 Padding 8,6,8,8
│   └── 版本号 Auto（11px / Meta）
└── 内容区
    ├── 拖拽栏 40
    └── 页面
        └── ScrollViewer
            └── StackPanel Margin="28,6,28,24"
                ├── 页标题（L1，下 20）
                ├── 卡片（下 16）
                ├── 卡片（下 16）
                └── …
```

侧栏导航项内部列宽：`3`（选中竖条）/ `26`（14 图标 + 12 间距）/ `*`。图标列固定 26 是为了让图标中线与文字起点天然对齐——只靠文字左边距的话，中英文混排时「调校」与「ZenForge」的视觉起点会差 1–2px。

---

## 5. 组件规范

### 5.1 卡片

| 项 | 值 |
|---|---|
| 样式 | `CardBorderStyle` |
| 圆角 / 内距 | 10 / `20, 22` |
| 底色 / 边框 | `Card.Background` / `Card.Border`（1px） |
| 卡片间距 | 外边距下 16 |
| 结构 | 标题（L2，下 14）→ 内容行 → 最后一行**必须去掉下边距** |

底部尾巴问题：卡片内每行用 `CardRowStyle`（下 20），最后一行要显式 `Margin="0"`，否则卡片底部多出 20px 空白。这是已知高频错误。

### 5.2 按钮

| 类型 | 样式 | 高度 | 内距 | 字号 | 用途 |
|---|---|---|---|---|---|
| 主按钮 | `PrimaryButtonStyle` | 32 | `14,0` | 13 SemiBold | 一屏一个，主操作 |
| 次按钮 | `SecondaryButtonStyle` | 32 | `14,0` | 13 | 常规操作，右侧间隔 8 |
| 页头按钮 | `HeaderActionButtonStyle` | 28 | `14,0` | 12 | 卡片标题右侧 |
| 图标按钮 | `IconButtonStyle` | 28×28 | 0 | — | 行内操作 |
| 方形图标按钮 | `SquareIconButtonStyle` | 32×32 | 0 | — | 独立图标动作 |
| 链接按钮 | `LinkButtonStyle` | Auto | 0 | 12 | 文字链接 |

- 按钮组右侧间距统一 8（样式里已带 `Margin="0,0,8,0"`），最后一个要清掉。
- 圆角 6，背景走 `Button.Neutral.*`，**不跟随强调色**。
- 图标按钮内容是 `Segoe MDL2 Assets` 字形或矢量 `Path`；矢量优先（不依赖字体）。

### 5.3 输入框

| 项 | 值 |
|---|---|
| 样式 | `AppTextBoxStyle`（全局隐式也已套用） |
| 高度 / 圆角 / 内距 | 32 / 6 / `10,0` |
| 字号 | 13 |
| 底色 | `Control.Background`（**不是** `Input.Background`） |
| 边框 | `Input.Border` → 悬停 `Input.BorderHover` → 聚焦 `AccentBrush` |
| 光标 / 选区 | `CaretBrush` = `AccentBrush`，`SelectionBrush` = `AccentBrush`，`SelectionOpacity` = **0.28** |

`SelectionOpacity` 必须小于 0.3：中文输入法组字时整段会被选中，实心色块会让用户看不见下面的字。

底色为什么不用 `Input.Background`：`#33000000` 叠在窗口底上比卡片还暗，输入框会读成「在卡片上挖了个洞」，与旁边的滑杆、开关割裂。

### 5.4 搜索框（自定义控件）

| 项 | 值 |
|---|---|
| 类型 | `zenforge:SearchBox`（`Controls/SearchBox.cs`） |
| 模板 | `Themes/SearchBox.xaml` |
| 高度 / 宽度 | 32 / 页面指定（进程页 180） |
| 几何 | 三列 `10 / 14 / *`；图标 14×14；输入框 `Padding="32,0,10,0"`（= 10 + 14 + 8） |
| 占位符 | `Margin="32,0,10,0"`，**与输入框 Padding.Left 同值**，因此占位文字首字与光标起点共用同一像素 |
| 图标色 | 默认 `Text.MetaBrush`，聚焦 `AccentBrush` |
| 事件 | `TextChanged` / `InputKeyDown`（**不是** `KeyDown`） |

**几何只在这个模板里存一份，页面不许再改间距。** 历史上页面各自摆图标与占位符，同一个 `32` 被用在两个坐标系（输入框内部 Padding vs 外层 Grid 的 Margin），导致进程页文字左移压到放大镜、日志页文字右移从中间开始。

### 5.5 下拉框

| 项 | 值 |
|---|---|
| 样式 | `AppComboBoxStyle` / 表格内 `TableCellComboStyle` |
| 高度 / 圆角 / 内距 | 32 / 6 / `10,0` |
| 下拉项 | `AppComboBoxItemStyle`：MinHeight 30，内距 `10,7`，字号 13 |
| 展开箭头 | 矢量 `Path`，**不用字形** |
| 下拉项悬停 | `List.Hover`；选中 `AccentSoftBrush` + `AccentBrush` 左边条 |
| 弹出层 | 必须显式设 `Menu.Background`，否则系统默认白底 |

表格内的文本必须 `TextTrimming="CharacterEllipsis"`，长优先级名不能撑破列宽。

### 5.6 开关

| 项 | 值 |
|---|---|
| 样式 | `ToggleSwitchStyle` |
| 尺寸 | 36 × 20 |
| 轨道 | 关闭 `Toggle.Off`，开启 `AccentBrush`，圆角 10 |
| 滑块 | `Toggle.Thumb`，浅色下加 `Toggle.ThumbBorder` 描边 |
| 动画 | 滑块位移，短时长（≤150ms），不做弹跳 |

### 5.7 滑块

**通用滑块**（隐式样式，`TargetType="Slider"`，无 `x:Key`）

| 项 | 值 |
|---|---|
| 高度 | 24 |
| 轨道 | 高 **4**，圆角 **2**，底色 `Toggle.Off` |
| 已选区 | **没有**。轨道整条同色，不做「已选部分换强调色」 |
| 滑块 | 圆形 **16×16**，`AccentBrush`，无描边 |
| 页面覆盖 | 允许。核心停放滑块写成 `Height="32"`，以便与同行的 32 高输入框对齐 |

**色相滑杆**（`HueSliderStyle`，色相专用，**故意带 `x:Key` 不走隐式**，避免其它 `Slider` 继承）

| 项 | 值 |
|---|---|
| 高度 | 24 |
| 轨道 | 高 6，圆角 3，彩虹渐变（`0/60/120/180/240/300/360`，亮度固定 70%） |
| 滑块 | 圆形 **18×18**，纯白填充 + 2px `#80000000` 描边（深色底上才看得见边界） |

**提交时机**：拖动过程只更新显示数值，**松手才提交**（`PreviewMouseUp` → 写硬件）。拖动中持续写 SMU 会导致命令堆积。数值显示用 L3 等宽字体。

### 5.8 徽章 / Pill

| 类型 | 样式 | 尺寸 | 内距 | 圆角 |
|---|---|---|---|---|
| 状态徽章 | `StatusBadgeStyle` | 高 28 | `14,0` | 6 |
| 日志级别 pill | `LogPillStyle` | 高 26 | `12,0` | 13 |

徽章底色必须比所在卡片**亮**一档（深色用 `Badge.Background` `#14FFFFFF`）。用 `List.Header` 会显得像挖了个洞。

### 5.9 Toast

| 项 | 值 |
|---|---|
| 宿主 | `Controls/ToastHost`，挂在主窗口内容区顶部 |
| 卡片宽度 | MinWidth 220 / MaxWidth 460，水平居中 |
| 圆角 / 内距 / 边框 | **10** / `0,12,0,12` / 1px |
| 卡片间距 | 8（多条依次向下排，不顶掉前一条） |
| 左侧色条 | 宽 **3**，圆角 `2,0,0,2`，占满卡片高度 |
| 图标 | 矢量 `Path`，16×16，`StrokeThickness=1.8`，外边距 `12,0,10,0` |
| 文字 | 13px，`LineHeight=19`，自动换行，右外边距 16 |
| 投影 | `BlurRadius=24`、`ShadowDepth=2`、`Direction=270`、`Opacity=0.45` |
| 位置 | 顶部外边距 16（宿主自带）+ 主窗口 `Margin="0,0,0,26"` |
| 停留 | 3 秒；鼠标悬停延长到 9 秒 |
| 进出动画 | 200ms 进（`EaseOut`，从 `-16px` 位移推入）/ 250ms 出 |

**Toast 配色（刻意只有三色）**

| 类型 | 颜色键 | 符号 |
|---|---|---|
| 成功 | `Log.Info`（灰蓝） | ✓ |
| 信息 | `Log.Info` | i |
| 警告 | `Log.Warn` | ! |
| 错误 | `Log.Error` | ✕ |

**成功提示不用绿色**，这是刻意的：保存成功是这类工具的常态，每次都给一块绿会让人对提示脱敏；而绿色在深色主题下与强调色系容易混淆。只有「需要注意」和「出事了」才值得用颜色抢注意力。

**判据：用户必须读完并做决定吗？** 是 → 模态对话框；否 → Toast。

- Toast：保存成功、已复制、主题已切换、参数已应用
- 对话框：全局重置、参数严重越界、写入失败、未处理异常兜底

### 5.10 对话框

| 项 | 值 |
|---|---|
| 类型 | `Views/DarkDialog`（替代系统 MessageBox，系统框无法改色） |
| 尺寸 | 420 宽，MinHeight 180，SizeToContent=Height |
| 圆角 | 12（标题栏 `12,12,0,0`） |
| 图标 | 圆形色底 28 + 符号，配色按类型取同色系（浅色主题用浅调底 + 深调符号） |
| 按钮 | 右下角，次按钮在左、主按钮在右，间距 8 |
| 居中 | 相对 `App.RealMainWindow`，**不能**用托盘那个 1×1 宿主窗口 |

### 5.11 表格 / 列表

| 项 | 值 |
|---|---|
| 行样式 | `ProcessRowStyle`：圆角 6，内距 `12,10`，透明底，悬停 `Nav.Button.Hover` |
| 行高 | **不写死**。由内容（13px 主行 + 11px 副行）+ 内距 `12,10` 撑开，实际约 52 |
| 主列文字 | `TableCellMainStyle`（L4，SemiBold） |
| 副列文字 | `TableCellSubStyle`（L6，Meta 色） |
| 表头 | 字号 12，内距 `10,8`，颜色 Meta |
| 进程页列宽 | `3*` / `7*` / `72` / `150` / `128` |
| 进程列表可视区 | 固定高 320（内层 `ScrollViewer`，虚拟化开启） |
| 规则区可视区 | 固定高 300 |
| 行分隔 | 不用横线，靠 2px 行间距 + 圆角行底 |
| DataGrid（另一套实现） | `DarkDataGridStyle`：`RowHeight=32`，单元格内距 `10,0` |

进程列表是自绘 `Grid` + `Border`，**不是** `DataGrid`，所以行高由内容撑开、不受 `RowHeight` 约束。两套实现不要混用。

**列表更新必须增量**：`Move` / `Insert` / `Remove`，禁止 `Clear()` + 重新 `Add`。后者会重建设施，造成闪屏、滚动跳变、焦点丢失、下拉框被强制关闭。

### 5.12 滚动条

| 项 | 值 |
|---|---|
| 宽 / 高 | 6 / 6 |
| 滑块 | `Scrollbar.Thumb`，圆角 3，左右外边距 2 |
| 悬停 | `Scrollbar.ThumbHover` |
| 拖动 | `AccentBrush` |
| 箭头按钮 | 隐去（`RepeatButton` 模板为透明空 Border） |

---

## 6. 状态与交互

### 6.1 四态（每个可交互控件都必须有）

| 状态 | 视觉表达 |
|---|---|
| 默认 | 静态底色 + 1px 边框 |
| 悬停 | 底色提亮一档（`*Hover` 键），或边框提亮（`Input.BorderHover`） |
| 聚焦 | 边框转 `AccentBrush`（输入类），或 `AccentSoftBrush` 底 + 左边条（导航类） |
| 禁用 | `Disabled.Background` / `Disabled.Foreground`，或整块 `Opacity=0.4` |

### 6.2 焦点框：**一律禁用系统虚线框**

WPF 默认的焦点虚线框（白底 + 浅蓝高亮 + 方框）在深色界面上极其刺眼。处理方式：

1. 具名样式里加 `<Setter Property="FocusVisualStyle" Value="{x:Null}"/>`；
2. `App.xaml.cs` 的 `DisableFocusVisuals()` 在启动时遍历所有资源字典，为每个 `Control` 派生类型的 `Style` 补上该 Setter。

**不要在 `App.xaml` 里写「只设一个 FocusVisualStyle 的空样式」。** WPF 的隐式样式按 `DefaultStyleKey` 精确匹配后**直接替换**，不与已合并字典里的同类型隐式样式合并。写一个空样式会把 `Themes/Controls.xaml` 里的完整深色模板整个顶掉，控件回落成系统默认白底。历史上三次「白块」事故全部源于此。

### 6.3 交互保护

用户正在操作列表时（下拉框展开、键盘焦点在行内、鼠标按下未松），**推迟**后台刷新，手松开再补一帧。否则用户伸手去点某一行的下拉框，正好赶上刷新重排，会点空或绑错进程。

### 6.4 文案与措辞

| 场景 | 规则 | 实例 |
|---|---|---|
| 按钮 | 动词开头，2–6 字，不加标点 | `保存`、`取消`、`应用`、`删除`、`应用规则`、`管理掩码`、`清空文件和内存` |
| 空态 | 统一「暂无 + 名词」 | `暂无日志`、`暂无进程`、`暂无已保存规则` |
| 元数据 | 只写定位信息，不写解释 | 路径、时间戳、`v1.0.0-can10` |
| 错误 | **给中文原文 + 错误码**，不吞、不改写 | `拒绝访问（错误码 5）`、`进程已退出（错误码 1168）` |
| 功能不可用 | 只给一句可执行结论，原始原因进日志 | `当前电源方案不支持核心停放，此项已停用` |

三条硬要求：

1. **不把原始错误拼进界面。** 技术细节进日志（`LogService.Warn`），界面只留用户能据此行动的一句话。历史事故：屏幕上出现过「无法读取（当前电源方案不提供核心停放设置。），此项已停用」这种嵌套括号。
2. **不写「暂不支持」而不说为什么。** 要么给出原因，要么给出下一步（去哪个页面开）。
3. **同一含义只用一个词。** 「保存」不混「存储」，「删除」不混「移除」，「重置」不混「恢复默认」。

### 6.5 排序稳定性

按浮点数排序但按一位小数显示，会导致「两行都显示 0.0% 却互相换位」。**排序键必须按显示精度取整**：

```csharp
rows.OrderByDescending(g => Math.Round(g.CpuValue, 1))
    .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
```

同值时必须再按名称（或其他稳定键）定序，否则顺序不确定。

---

## 7. 动效

**全工程只有 Toast 有动画。XAML 里零 `Storyboard`、零 `DoubleAnimation`。**

| 场景 | 时长 | 实现 |
|---|---|---|
| Toast 进入 | 200ms | 代码动画：透明度 `0→1` + `TranslateTransform.Y` `-16→0`，`CubicEase EaseOut` |
| Toast 退出 | 250ms | 同上反向 |
| 其余全部 | **0（瞬变）** | 开关、滑块、列表行重排、页面切换、对话框打开，都不做过渡 |

界面是工具，不是演示。列表行做位移动画会让人眼追踪困难，反而更乱；开关加过渡会在快速连点时产生「看着没点到」的错觉。默认不加动画，要加必须说出它解决了什么问题。

---

## 8. 图标

| 优先级 | 形式 | 说明 |
|---|---|---|
| 1 | 矢量 `Path` | 独立于字体，缩放不失真。**新图标优先用这个** |
| 2 | `Segoe MDL2 Assets` 字形 | 仅窗口按钮等系统同源图标（`&#xE921;` 最小化等） |

- 图标尺寸：14 / 16 / 20 / 28。常用 14（行内）与 20（导航）。
- 图标必须与文字**垂直居中**，且与文字间距 8。
- 彩色图标不用字形，用矢量 + `Stroke`/`Fill` 绑资源键。
- `StrokeThickness` 建议 1.3–1.5，`StrokeStartLineCap`/`EndLineCap` = `Round`。

---

## 9. 工程约束（WPF 特有，违反必崩）

这几条不是风格偏好，是踩过事故后总结的硬约束。**编译期全部 0 警告，只在运行期炸。**

### 9.1 模板内禁止引用跨字典的 `StaticResource`

`ControlTemplate` / `DataTemplate` 的内容在**模板自身被解析时**求值，此刻没有元素树父级，`StaticResource` 的查找链只走到模板本身，**到不了 `Application.Resources`**：

```
XamlParseException: 无法找到名为"AppTextBoxStyle"的资源
```

而且它只在控件**首次 `ApplyTemplate`** 时才炸，启动、加载页面、编译期全部一声不响。

**正确做法，二选一**：
- 把外形直接内联写进模板（推荐，也是 `Themes/SearchBox.xaml` 的做法）；
- 用 `DynamicResource`——但**只用于画刷**，绝不用于 `Style`（`Style` 用 Dynamic 会在主题切换时重建模板，产生抖动）。

例外：挂在 `xx.ItemTemplate` / `xx.ContentTemplate` / `xx.CellTemplate` 上的 `DataTemplate` 是**合法**的——它由容器在已有元素树里实例化，沿树能查到。`ControlTemplate` 没有这个例外。

项目里 `tools/xamlcheck.py` 第 [5] 类检查专门抓这个。

### 9.2 资源字典之间互相看不见

`StaticResource` 的查找范围是「本字典 → 本字典合并的字典」，**不会向上查父字典**。`Themes/Controls.xaml` 与 `App.xaml` 的本地资源是**兄弟关系**，谁也看不见谁。

因此：**任何 `BasedOn` 另一个字典里样式的样式，必须定义在那个样式所在的字典里。** 历史上把 `BasedOn="{StaticResource AppTextBoxStyle}"` 写进 `Controls.xaml`，运行期拿到 `null`，抛 `XamlParseException`，表现为「点日志页即闪退」。

### 9.3 `GridLength` 不能用 `Double` 资源

`ColumnDefinition.Width` 接受 `GridLength`，`<sys:Double x:Key="X">32</sys:Double>` 在运行期会抛「不是有效值」。列宽只能写字面量或 `*` 比值。

### 9.4 跨线程更新 `ObservableCollection` 必须回 UI 线程

`async` 只会回到**调用处的** `SynchronizationContext`。若调用方是线程池线程（无 SyncContext），`await` 之后仍在后台线程执行，写绑定集合会抛「调用线程必须为 STA」。

- 采样/计算放在专用 STA 后台线程；
- 回 UI 用 `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)`；
- 重入保护用「忙则记账、算完补最新帧」，不要「忙则丢帧」（会漏掉最后一帧，表现为列表停更）。

### 9.5 事件转发不能用 `RaiseEvent`

在事件源自身的处理器里 `RaiseEvent` 同一参数，会沿同一路由再走一遍 → 无限递归 → `StackOverflow`。自定义控件转发键盘事件要用**独立 CLR 事件**（如 `SearchBox.InputKeyDown`）。

### 9.6 改完必须跑静态检查

```
python3 tools/xamlcheck.py
```

五类检查：XML 解析、资源类型与属性类型不符、容器结构、自定义控件属性/事件存在性、模板内不可达的 `StaticResource`。全部必须为 0。

---

## 10. 已知漂移（存在但**不应**在新代码中使用）

以下样式在代码里定义着，但全工程零引用。它们要么是旧版遗留，要么是被新层级取代后忘了删。新代码**不要**用它们当作「现成规范」：

`AppScrollBarPageButtonStyle`、`AppScrollBarThumbStyle`（已内联进滚动条模板）、`CardHintStyle`、`ConfigItemBorderStyle`、`DarkContextMenuStyle`、`DarkMenuItemStyle`、`DarkMenuSeparatorStyle`、`HeaderActionButtonStyle`、`LinkButtonStyle`、`LogPillStyle`、`NavButtonStyle`、`NeutralBadgeStyle`、`NeutralBadgeTextStyle`、`PageHeaderCardStyle`、`PageHeaderTitleStyle`、`SettingsGroupSeparatorStyle`、`SettingsSubLabelStyle`、`SettingsSubRowStyle`、`Text.MetaMono`、`Text.Numeric`、`WindowCloseButtonStyle`

其中三个需要特别说明：

- **`PageHeaderCardStyle` / `PageHeaderTitleStyle`**：定义了一整套「页头卡片」（高 76、内距 `20,0`、下边距 12），但**当前所有页面都没用**，实际页面是「裸标题 + 下 20」。不要以为项目里有页头卡片。
- **`Text.Numeric` / `Text.MetaMono`**：属于字号阶梯里的 L3 与 L6-mono，规范上有效，但当前代码里没有引用点。新代码可以做第一个使用者。
- **`HeaderActionButtonStyle` / `LogPillStyle` / `NavButtonStyle`**：视觉规范已定义（见 §4.3 / §5.8），实现也在，只是当前页面走的是别的写法。使用前先确认不要与页面现有实现重复。

---

## 11. 新增界面的落地检查清单

交付前逐条过：

- [ ] 所有颜色都是资源键，没有裸 `#RRGGBB`
- [ ] 字号来自 §3.2 阶梯，没有裸 `FontSize`
- [ ] 同屏并排的控件等高（对照 §4.3 的高度梯）
- [ ] 圆角来自圆角梯（3 / 4 / 6 / 8 / 10 / 12 / 13）
- [ ] 卡片最后一行清掉了下边距（没有 20px 尾巴）
- [ ] 每个可交互控件都有 hover / focus / disabled 三态
- [ ] 焦点虚线框已禁用（`FocusVisualStyle={x:Null}`）
- [ ] 深浅两套主题都看过，没有花斑、没有白块
- [ ] 125% 缩放下文字不截断、不重叠
- [ ] 模板里没有跨字典的 `StaticResource`
- [ ] 列表更新是增量 `Move`，不是 `Clear` + `Add`
- [ ] `python3 tools/xamlcheck.py` 五类全 0
- [ ] `dotnet build -c Release -p:EnableWindowsTargeting=true` 0 警告 0 错误

---

## 附：一句话记住的核心

> **颜色走键、字号走级、尺寸走样式；模板内不跨字典查资源，`App.xaml` 里不写不完整样式。**
