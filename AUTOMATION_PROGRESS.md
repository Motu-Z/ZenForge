# ZenForge 自动化开发进度（AUTOMATION_PROGRESS）

> 执行时间：2026-10-31 02:25 – 03:0x（自动挂机）｜ 基线：v1.0.3 ｜ 收尾：v1.0.4 + v1.1.x 骨架
> 工程路径：`/workspace/projects/ZenForge` ｜ 仓库：Motu-Z/ZenForge（main）

---

## 0. 环境准备

| 项 | 状态 | 说明 |
|---|------|------|
| 源码导入 + git 初始化 | ✅ | 工作树嫁接到远程 `main`(f228eb0)，**非强推** |
| 规范文档阅读 | ✅ | 全局交接文档 / 项目规范与开发手册 / UI-设计规范 / CHANGELOG |
| .NET 10 SDK | ✅ | 沙箱原为 6.0.301，已装 10.0.403 |
| 编译验证 | ✅ | `dotnet build -c Release -p:EnableWindowsTargeting=true`，**全程 0 警告 0 错误** |

## 提交与 Tag 流水（全部已推送）

| Tag | 内容 |
|-----|------|
| `v1.0.4-can1` | 进程列表平滑排序过渡（`Controls/SmoothSortPanel.cs`） |
| `v1.0.4-can2` | 高 DPI 字体适配（`Helpers/UiTextOptions.cs` + 位移行 Animated hinting） |
| `v1.0.4-can3` | 日志内存再收（条数 / 单条长度 / 落盘队列三重上限 + AutoResetEvent） |
| `v1.0.4-rc1` | 版本号 → 1.0.4、CHANGELOG、规范文档路线图对齐 |
| `v1.1.0-can1` | `ProfileService` 多方案骨架 |
| `v1.1.0-can2` | `ScenarioService` 场景切换骨架 + 配置页数据通路 |
| `v1.1.0-can3` | 修：生效中标记被 XAML 局部值压过（Visibility 移入样式 Setter） |

---

## 第一阶段：v1.0.4 表现打磨 — 全部完成

| # | 任务 | 状态 | 落点 |
|---|------|------|------|
| 1.1 | 进程列表平滑排序过渡 | ✅ | `Controls/SmoothSortPanel.cs`：继承 `VirtualizingStackPanel`，Arrange 后把渲染位置拉回旧位置再 200ms 缓动归零；滚动期间与容器复用时不补间；位置记录用 `ConditionalWeakTable`（容器被回收即自动消失，不泄漏） |
| 1.2 | 高 DPI 字体适配 | ✅ | `Helpers/UiTextOptions.cs`：按实际 DPI 选 `Ideal`/`Display`（>125% 走 Ideal），跟随 `DpiChanged`；进程行加 `TextHintingMode=Animated` |
| 1.3 | 日志内存再收 | ✅ | `LogService`：500→320 条、128→64 批量、新增单条 2000 字符上限（Debug/磁盘不截）、队列 2000→800、`ManualResetEventSlim`→`AutoResetEvent` |

## 第二阶段：v1.1.x 功能注入（骨架）— 完成

| # | 任务 | 状态 | 落点 |
|---|------|------|------|
| 2.1 | 配置页 UI 骨架 | ✅ | `Views/ProfilePage.xaml`：版式未动，两张卡接上列表 / 开关 / 三个动作；规则库双列继续灰着（编辑器未定） |
| 2.2 | Service 层数据结构与接口 | ✅ | `Services/ProfileService.cs`（方案快照 + 落盘 + 切换）、`Services/ScenarioService.cs`（触发源枚举 + 规则 + 仲裁 + `Notify` 入口） |
| 2.3 | 数据流转通路 | ✅ | UI → Service → 三个 Store → `TuningApplyService`；`ScenarioService.UiContext` 封送后台调用回 UI 线程；启动时（UI 模式 + `-silent`）各加载一次 |

---

## 失败与跳过记录（防死循环闸门）

| 现象 | 处理 | 结果 |
|------|------|------|
| `git push` 两次超时（github.com:443 TLS 连接中断） | 本地提交与 Tag 照常生成，跳过本次推送，继续下一个任务 | 后续重试成功，can1~can3 / rc1 全部已推送，无提交丢失 |
| 沙箱无 .NET 10 SDK（仅 6.0.301），`net10.0-windows` 无法编译 | 装 .NET 10.0.403 + `EnableWindowsTargeting` | 基线即 0 警告 0 错误，全程真编译验证 |
| 配置页「生效中」圆点：XAML 局部值 `Visibility="Collapsed"` 优先级高于 `DataTrigger` | 已修（移入 Style Setter） | 编译通过，已提交 `v1.1.0-can3` |

> 无一步触发 3 次修复闸门；所有改动均一次编译通过。

## 当前代码状态

- 版本号：csproj 三处 = **1.0.4**（骨架不改版本号，CHANGELOG 记在 `[Unreleased]`）
- 工作树：干净（全部已提交并推送）
- 未做 / 待办：见 CHANGELOG `[Unreleased]` 段四条（现存进程立即绑核、事件源接入、规则编辑器、失败回滚）
