# Zenwik 自动化开发进度（AUTOMATION_PROGRESS）

> 执行时间：2026-10-31 02:25 – 11:xx（自动挂机 + 白天封版）
>
> ## 第四轮（v1.2.0 封版）结果速览
>
> | Tag | 内容 | 编译 |
> |---|---|---|
> | `v1.2.0-can1` | 关窗即销毁（废弃秒开）+ 静态引用/定时器解绑 + 二次内存回收 | 0/0 |
> | `v1.2.0-can2` | `OpenProcess` 最小权限 + 同类失败 60 秒节流（`LogThrottle`） | 0/0 |
> | `v1.2.0-can3` | 调校页三卡标题中文化并统一 L2、SMU 改「已联通」、优先级档位汉化 | 0/0 |
> | `v1.2.0-can4` | 搜索框：占位符绑定缺失 / 光标垂直偏移 / 聚焦即消失 | 0/0 |
> | `v1.2.0-can5` | 抽离 `Zenwik.Core`（零 WPF 引用，`dotnet build` 单工程可验证） | 0/0 |
> | `v1.2.0-can6` | 文档：CHANGELOG、封版交接文档、规范路线图、版本号 1.2.0 | 0/0 |
>
> 未验证项（沙箱为 Linux，无 Windows 运行时）：关窗后真实内存、动画观感、高 DPI 渲染、
> 搜索框光标像素位置。代码路径与清理点已在 `docs/代码审查报告-v1.0.4.md` 与 CHANGELOG 中列明。
> 工程路径：`/workspace/projects/Zenwik` ｜ 仓库：Motu-Z/Zenwik（main）
> 编译口径：`dotnet build -c Release -p:EnableWindowsTargeting=true`，**全程 0 警告 0 错误**

---

## 0. 环境准备

| 项 | 状态 | 说明 |
|---|------|------|
| 源码导入 + git 初始化 | ✅ | 工作树嫁接到远程 `main`(f228eb0)，**非强推**，v1.0.3 历史完整保留 |
| 规范文档阅读 | ✅ | 全局交接文档 / 项目规范与开发手册 / UI-设计规范 / CHANGELOG |
| .NET 10 SDK | ✅ | 沙箱原为 6.0.301，已装 10.0.403（文档 §5 指定的 Linux 真编译验证路径） |

---

## 1. 提交与 Tag 流水（全部已推送到 GitHub，未创建 Release）

### 第一阶段 v1.0.4 表现打磨

| Tag | 内容 |
|-----|------|
| `v1.0.4-can1` | 进程列表平滑排序过渡（`Controls/SmoothSortPanel.cs`） |
| `v1.0.4-can2` | 高 DPI 字体适配（`Helpers/UiTextOptions.cs` + 位移行 Animated hinting） |
| `v1.0.4-can3` | 日志内存再收（条数 / 单条长度 / 落盘队列三重上限 + AutoResetEvent） |
| `v1.0.4-rc1` | 版本号 → 1.0.4、CHANGELOG、规范文档路线图对齐 |

### 第二阶段 v1.1.x（骨架 → 实装）

| Tag | 内容 |
|-----|------|
| `v1.1.0-can1` | `ProfileService` 多方案骨架 |
| `v1.1.0-can2` | `ScenarioService` 场景切换骨架 + 配置页数据通路 |
| `v1.1.0-can3` | 修：生效中标记被 XAML 局部值压过（`Visibility` 移入样式 Setter） |
| `v1.1.0-can4` | CHANGELOG Unreleased 段、交接文档与进度归档 |
| `v1.1.0-can5` | **代码审查修正 6 项 + 事件源实装**（见 §2、§3；两者合并在一笔提交里，属记录瑕疵） |
| `v1.1.0-can6` | 修：场景切换短路比自己记的目标 → 用户手动切换被跳过 |
| `v1.1.0-can7` | 审查报告 + CHANGELOG 实装记录 + 交接文档待办更新 |
| `v1.1.0-can8` | 修：`ScenarioMonitor.Sync` 订阅的 check-and-set 挪进锁内 |

---

## 2. 深度代码审查（v1.0.4-rc1）

完整报告见 **`docs/代码审查报告-v1.0.4.md`**。结论速览：

| # | 位置 | 问题 | 等级 | 处置 |
|---|------|------|------|------|
| 1 | `SmoothSortPanel` | 滚动抑制可能**永久失效**（`Loaded` 找不到 ScrollViewer 就不再重试） | 高 | ✅ 已修 |
| 2 | `SmoothSortPanel` | 位移上限写死 480px，大窗口/高 DPI 下小于一屏 → 时灵时不灵 | 中 | ✅ 已修 |
| 3 | `SmoothSortPanel` | `ScrollViewer` 强引用面板，`Unloaded` 未解绑 | 低 | ✅ 已修 |
| 4 | `SmoothSortPanel` | `RequestSmooth` 只清**第一个**面板，页面有两个列表会清错 | 中 | ✅ 已修 |
| 5 | `LogService.Clear()` | 队列/代号推进押后在 `Post` 里 → 旧日志写回刚删的文件 | 中 | ✅ 已修 |
| 6 | `App.xaml.cs` | `_signatureDialogCount` 无上限，签名无界 → 慢泄漏 | 低 | ✅ 已修 |
| 7 | `ScenarioService` | 场景切换短路比错对象 → 用户手动切换被跳过 | 高 | ✅ 已修 |
| 8 | `SmoothSortPanel` | 布局死循环 | — | ⚪ 结论：不存在（改的是 `RenderTransform`，不参与布局） |
| 9 | `SmoothSortPanel` | 内存泄漏 | — | ⚪ 结论：无泄漏路径（`ConditionalWeakTable` + `FillBehavior.Stop`） |
| 10 | Service/Helper 层 | 硬编码颜色 | — | ⚪ 结论：无违规（命中的都是合法的颜色生产者） |

**顺带澄清一处易被误判的**：`_signatureDialogCount` 看似无锁访问有并发风险，实际安全 ——
它只在 `HandleUiException` 内被访问，而该函数**只由 UI 线程的 Dispatcher 通道进入**；
`AppDomain` / `TaskScheduler` 两条后台通道走的是各自的落盘分支，不碰它。
真正需要锁的 `_signatureTotal` 已有 `lock` 保护。

---

## 3. v1.1.x 底层实装（本轮新写入）

| 文件 | 职责 |
|---|---|
| `Services/PowerStatusWatcher.cs`（新） | 供电状态监听：`SystemEvents.PowerModeChanged` 为主 + 30 秒轮询兜底（前者依赖消息泵且部分 OEM 不广播）。P/Invoke `GetSystemPowerStatus`，不引 WinForms |
| `Services/ScenarioMonitor.cs`（新） | 编排层：把进程事件与电源通知接到 `ScenarioService.Notify`。三条约束：不启用就零占用 / 按需订阅 / 防抖在调用侧（800ms） |
| `Services/ScenarioSelfCheck.cs`（新） | `-selftest` 自检：仲裁、监听范围一致性、快照深拷贝、生效标记唯一。**不下发任何硬件** |
| `Services/ProcessWatcher.cs` | 新增 `ProcessSetChanged` 事件 + `ReapplyNow()`（现存进程按新规则重扫） |
| `Services/ProfileService.cs` | `Apply` 写回三个 store 后调 `ReapplyNow()` |
| `Services/ScenarioService.cs` | 新增 `HasProcessRules` / `HasPowerRules`；修短路逻辑；日志补触发源 |

---

## 4. 沙箱内实跑的逻辑验证

两段 UI 上点不出来的纯逻辑做了**逻辑等价复制**后在 Linux 上实跑（临时工程在 `/tmp`，不进仓库）：

```
1. 规则仲裁    6 项全过（优先级 / 禁用 / 大小写 / 空目标 / 落空 / 传全量）
2. 增量对齐    9 项全过（倒序 / 删头加尾 / 头部插入 / 清空 / 从空填充 / 幂等 …）
结果：通过 15，失败 0
```

---

## 5. 未做 / 做不到的事（如实记录）

| 项 | 原因 |
|---|---|
| 场景规则编辑器 | 本轮纪律要求不碰 UI |
| 切换失败回滚 | 需要「部分写成功」的回滚顺序（先 FMax 后 CO）的**实测依据**，不猜 |
| 重扫的解绑语义 | 不再命中规则的进程保持上一次绑核；恢复默认要显式写回系统默认 CPU Set，而「默认」随拓扑变化 |
| **平滑动画与高 DPI 的视觉效果** | **沙箱是 Linux，无 Windows 运行环境，无法验收**。请真机确认 |
| `Paused`/`Verbose` 未加 volatile | 语义容忍晚一拍，加了反而误导 |

---

## 6. 明早验收建议

1. `git pull` 后编译，确认 0 警告 0 错误；
2. 加 `-selftest` 启动一次，日志里看「场景自动切换 · 底层自检」段落是否全通过；
3. 平滑动画与高 DPI：需要真机看（Linux 沙箱无法验收）；
4. 场景自动切换：配一条「运行某进程 → 某方案」规则，启动该进程看日志是否切了、且现存进程是否重扫。

---

# 轮次归档：v1.2.1 · 更名 Zenwik + SMU 换 1.93.1 本地 DLL

> 2026-10-31 11:47 起。依据用户对交接文档 10 个问题的拍板执行。

## 1. 执行结果

| # | 任务 | 结果 |
|---|---|---|
| 1 | `ZenForge` → `Zenwik` 全量更名 | ✅ 解决方案 / 工程 / 程序集 / 命名空间 / `%AppData%` / UI 文案 / 图标 / GitHub 仓库 |
| 2 | Core 命名空间下沉 `Zenwik.Core.*` | ✅ WPF 层依赖改为 `Zenwik.csproj` 里两条全局 `using`（两侧无同名类型，无歧义） |
| 3 | 移除 `LLT.ZenStates-Core`，改本地 DLL | ✅ `libs/ZenStates-Core/`（`net8.0-windows` 资产），适配 `Cpu` 构造变化 |
| 4 | 0 警告 0 错误 | ✅ `dotnet build Zenwik.slnx -c Release`：0 / 0 |
| 5 | 交接文档定稿 v1.0.1 | ✅ §9 改为决策记录；新增本地 DLL 与 AOT 注意事项 |
| 6 | 打 Tag 推送 | ✅ `v1.2.1` |

## 2. 1.93.1 的破坏性变化（已用反射逐个比对 API）

用 `MetadataLoadContext` 把 1.92.4 → 1.93.1 的公开面拉出来逐项比：

| 项 | 变化 | 处理 |
|---|---|---|
| `Cpu..ctor` | 无参 → `Cpu(CoreOptions options)` | 改 `new Cpu(CoreOptions.Current)`（两处：`RetryAvailabilityProbe` / `TryCreate`） |
| `SendTestMessage` | `()` → `(uint arg = 0, Mailbox mbox = null)` | 两参数均可选，老写法零改动 |
| `smu.SMU_TYPE` / `smu.Version` / `smu.Rsmu.SMU_MSG_*` | 未变 | — |
| 拓扑 `info.topology.*` | 未变（`coresPerCcx` 仍在） | — |
| `GetPsmMarginSingleCore` / `SetPsmMarginSingleCore` / `MakeCoreMask` / `GetFMax` / `SetFMax` | 未变 | — |
| 传递依赖 | NuGet 时代由包带入 `System.ServiceProcess.ServiceController` | **显式声明**，否则编译失败 |
| 原生依赖 | `WinIo32.dll` 按文件名 `LoadLibrary` | `ExcludeFromSingleFile=true`，随 exe 落地 |

## 3. 本轮踩到的坑（给下一任省时间）

1. **批量改名的连带伤害**：`"ZenForge"` 这个**目录名字符串**在 9 个文件里各写了一遍，
   统一替换后变成 `"Zenwik.Core"`（把命名空间前缀写进了路径）。已用 `AppPaths` 收口，
   顺带把这类重复彻底消掉。
2. **同命名空间隐式可见**：Core 下沉一层后，几十个 WPF 文件失去对 `LogService` 的隐式访问。
   逐文件补 `using` 会让 60 个文件各多一行噪音 —— 改为项目级两条全局 `using`。
3. **`Services.LogService` 这类相对限定名**：位于 `namespace Zenwik` 内时解析到 `Zenwik.Services`，
   必须写全 `Zenwik.Core.Services.LogService`。编译器会直接报 CS0234，好定位。

## 4. 待真机确认

- CO 逐核写入 + 回读一致、FMax 写入 + 回读容差、SMU 版本号显示（1.93.1 通道）；
- 升级后配置是否从 `%AppData%\ZenForge` 成功搬到 `%AppData%\Zenwik`；
- 旧的 `ZenForgeAutoStart` 计划任务是否需要手动删（不会自动清理）。
