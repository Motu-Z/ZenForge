using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Zenwik.Core.Helpers;

namespace Zenwik.Core.Services
{
    /// <summary>
    /// 场景触发源。
    ///
    /// 只列「能靠一个事件判定、且判定成本≈0」的源。
    /// CPU 占用率、温度这类需要采样的量刻意不在这里：它们要常驻轮询才有意义，
    /// 而本工程对后台的硬约束是「没人看的时候占用必须归零」——
    /// 为一个「负载高就换方案」的功能赔上常驻采样，不划算。
    /// </summary>
    public enum ScenarioTrigger
    {
        /// <summary>指定进程正在运行（参数 = 进程名，如 game.exe）。进程退出即视为不再命中。</summary>
        ProcessRunning = 0,

        /// <summary>电池供电（笔记本拔掉电源）。</summary>
        OnBattery = 1,

        /// <summary>交流供电（插上电源）。</summary>
        OnAcPower = 2
    }

    /// <summary>
    /// 一条场景规则：什么情况 → 切到哪套方案。
    /// </summary>
    public sealed class ScenarioRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public ScenarioTrigger Trigger { get; set; } = ScenarioTrigger.ProcessRunning;

        /// <summary>
        /// 触发参数。进程类触发填进程名；电源类触发留空。
        /// 用一个字符串而不是给每种触发各开一个字段：新增触发源时不必改数据结构，
        /// 代价只是约定写在注释里。
        /// </summary>
        public string Argument { get; set; } = string.Empty;

        /// <summary>命中后要切换到的方案 Id（指向 <see cref="ProfileService"/> 中的方案）。</summary>
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>
        /// 仲裁优先级：同时命中多条规则时，数字小的赢。
        ///
        /// 为什么需要：插着电打游戏时「插电」和「游戏在跑」两条都命中，
        /// 没有仲裁就会看规则在列表里的先后，而那个顺序取决于用户哪条先建 ——
        /// 不可预测的规则等于没有规则。
        /// </summary>
        public int Priority { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>触发源文案（列表行左侧）。写法与 BindingRule.Label 一致：显示名归模型管。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string TriggerText => Trigger switch
        {
            ScenarioTrigger.ProcessRunning => string.IsNullOrEmpty(Argument) ? "进程运行" : $"运行 {Argument}",
            ScenarioTrigger.OnBattery => "电池供电",
            _ => "交流供电"
        };

        /// <summary>
        /// 目标方案名。方案被删后显示「（已删除）」而不是空白 ——
        /// 空白会被读成「这条规则没配方案」，而实际是配过的那个没了。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string ProfileName => ProfileService.FindById(ProfileId)?.Name ?? "（已删除）";
    }

    /// <summary>scenarios.json 的根对象。</summary>
    public sealed class ScenarioFile
    {
        public bool Enabled { get; set; }

        public List<ScenarioRule> Rules { get; set; } = new();
    }

    /// <summary>
    /// 场景自动切换（v1.1.x 骨架）。
    ///
    /// 【这一版交付到哪一步】
    /// 数据结构、落盘、仲裁逻辑、以及唯一的外部入口 <see cref="Notify"/> 都已可用；
    /// 事件源尚未接入 —— 进程事件要挂到 ProcessWatcher 上，电源状态要挂到系统的
    /// 电源模式变更通知上，两处都牵动常驻线程的生命周期，属于 v1.1.x 的实装项。
    ///
    /// 【为什么不先接事件源】
    /// 接了就要回答「什么时候启停监听」：后台服务关掉时监听要不要停、
    /// 窗口收进托盘后还要不要切方案。这些问题的答案取决于用户对「后台常驻」的预期，
    /// 先把判定逻辑做成纯函数，接事件就只剩一行调用 —— 顺序反了才会返工。
    ///
    /// 【后台零占用】
    /// 本类不持有任何定时器、不启动任何线程。所有状态变化都来自外部调用 Notify。
    /// </summary>
    public static class ScenarioService
    {
        private static readonly string FilePath = AppPaths.File("scenarios.json");

        private static readonly object Sync = new();

        /// <summary>界面绑定的规则集合。</summary>
        public static ObservableCollection<ScenarioRule> Rules { get; } = new();

        /// <summary>规则或总闸变化时触发。</summary>
        public static event Action? Changed;

        /// <summary>
        /// 总闸。关掉时 <see cref="Notify"/> 直接返回，一条规则都不评估。
        /// </summary>
        public static bool Enabled { get; private set; }

        /// <summary>最近一次实际切换到的方案 Id（供界面显示当前场景出自哪条规则）。</summary>
        public static string? LastAppliedProfileId { get; private set; }

        /// <summary>
        /// 是否存在启用中的「进程运行」类规则。
        ///
        /// 【为什么监听方要问这个】
        /// 一个用户可能只配了「插电 → 性能方案」这一条电源规则。若监听方不分青红皂白
        /// 把进程事件也挂上，那么机器上每一个进程的启动都会换来一次全量进程名单采集
        /// —— 而采集名单是为了判定进程规则，一条都没配时它是纯浪费。
        /// 「要监听什么」由规则内容决定，不是由「场景功能开着」决定。
        /// </summary>
        public static bool HasProcessRules
        {
            get
            {
                lock (Sync)
                    return Rules.Any(r => r.Enabled && r.Trigger == ScenarioTrigger.ProcessRunning);
            }
        }

        /// <summary>是否存在启用中的电源类规则（电池 / 交流）。同上，决定要不要挂电源监听。</summary>
        public static bool HasPowerRules
        {
            get
            {
                lock (Sync)
                    return Rules.Any(r => r.Enabled &&
                        (r.Trigger == ScenarioTrigger.OnBattery || r.Trigger == ScenarioTrigger.OnAcPower));
            }
        }

        /// <summary>
        /// UI 线程的同步上下文，由 App.xaml.cs 在启动时注入（与 LogService.UiContext 同一处）。
        ///
        /// 【为什么需要它】
        /// 场景切换的调用方是后台的进程事件 / 电源通知线程，而切换最终要动
        /// ProfileService 里那个 ObservableCollection —— 跨线程改集合是直接抛
        /// InvalidOperationException 的。封送责任放在后台入口这一侧：
        /// 谁把调用带到了别的线程，谁负责把它送回来。
        /// </summary>
        public static SynchronizationContext? UiContext { get; set; }

        public static void Load()
        {
            lock (Sync)
            {
                ScenarioFile? file = null;
                try
                {
                    file = JsonFile.Read<ScenarioFile>(FilePath);
                }
                catch (Exception ex)
                {
                    LogService.Error($"读取场景规则失败: {ex.Message}");
                }

                Enabled = file?.Enabled ?? false;

                // 增量对齐，不用 Clear 重建（Clear 的 Reset 通知会让列表整列闪烁）
                List<ScenarioRule> desired = file?.Rules ?? new List<ScenarioRule>();
                for (int i = Rules.Count - 1; i >= 0; i--)
                    if (!desired.Any(r => r.Id == Rules[i].Id))
                        Rules.RemoveAt(i);

                for (int i = 0; i < desired.Count; i++)
                {
                    int index = IndexOfId(desired[i].Id);
                    if (index == -1)
                        Rules.Insert(i, desired[i]);
                    else if (index != i)
                        Rules.Move(index, i);
                }
            }

            Changed?.Invoke();
        }

        /// <summary>切换总闸。</summary>
        public static void SetEnabled(bool enabled)
        {
            lock (Sync)
            {
                if (Enabled == enabled)
                    return;

                Enabled = enabled;
                Persist();
            }

            LogService.Info(enabled ? "场景自动切换：已开启" : "场景自动切换：已关闭");
            Changed?.Invoke();
        }

        /// <summary>新增或按 Id 覆盖一条规则。</summary>
        public static void Save(ScenarioRule rule)
        {
            lock (Sync)
            {
                int index = IndexOfId(rule.Id);
                if (index >= 0)
                    Rules[index] = rule;
                else
                    Rules.Add(rule);

                Persist();
            }

            Changed?.Invoke();
        }

        public static bool Delete(string id)
        {
            lock (Sync)
            {
                int index = IndexOfId(id);
                if (index < 0)
                    return false;

                Rules.RemoveAt(index);
                Persist();
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 外部事件源的唯一入口：告诉服务「此刻有哪些场景成立」。
        ///
        /// 传的是**全部当前成立的触发**而不是单个事件：进程 A 退出时，
        /// 进程 B 可能还在跑，只看「A 退出了」会误判成「没有进程命中」。
        ///
        /// 【v1.1.x 的接入方式】
        ///   · 进程：ProcessWatcher 每次扫描/事件回调时，把当前存活的进程名集合
        ///     转成 ProcessRunning 触发传入；
        ///   · 电源：系统电源模式变更通知回调时，按当前供电状态传 OnBattery / OnAcPower。
        ///
        /// 【尚未做的防抖】
        /// 游戏启动会瞬间拉起十几个子进程，逐个通知会触发十几次评估。
        /// 实装时应在调用侧合并（同一次扫描只调一次），而不是在这里加延时 ——
        /// 延时窗口取多长没有依据，只能靠猜。
        /// </summary>
        public static void Notify(IEnumerable<(ScenarioTrigger Trigger, string Argument)> active)
        {
            if (!Enabled)
                return;

            // 后台线程进来就原样封送一次再走：后面的评估与切换都要动 UI 侧的集合。
            if (UiContext != null && SynchronizationContext.Current != UiContext)
            {
                List<(ScenarioTrigger Trigger, string Argument)> copy = active.ToList();
                UiContext.Post(_ => Notify(copy), null);
                return;
            }

            string? target = Resolve(active);

            // 没有规则命中时**刻意什么都不做**。
            // 场景只负责「切到某套」，不负责「切回去」——
            // 用户手动选的那套不该被一条没命中的规则覆盖掉。
            if (target == null)
                return;

            // 【为什么比的是 ProfileService.ActiveId，而不是自己记的 LastAppliedProfileId】
            // 「上次是场景切的」不等于「现在还是那套」：中间用户可能手动切过。
            // 比自己那份会出现这样的现场 —— 插电时场景切到「性能」，用户手动切到
            // 「静音」，再拔插一次电源，场景一看 target 和上次一样就跳过了，
            // 于是用户的手动选择被无声地保留下来，而他期待的是场景重新接管。
            // 比「当前实际生效的是哪套」，两种来源的切换都算数，才不会短路错。
            bool alreadyActive = string.Equals(target, ProfileService.ActiveId, StringComparison.Ordinal);
            LastAppliedProfileId = target;

            if (alreadyActive)
                return;

            if (ProfileService.Apply(target))
                LogService.Info($"场景切换：{Describe(target)} ← {DescribeHit(active)}");
            else
                LogService.Warn("场景切换：目标方案不存在，已跳过（方案可能已被删除）");
        }

        /// <summary>
        /// 仲裁：从命中的规则里挑出该生效的那条。纯函数，不落盘、不下发，
        /// 因此界面可以拿它做「当前会切到哪套」的预览。
        /// </summary>
        public static string? Resolve(IEnumerable<(ScenarioTrigger Trigger, string Argument)> active)
        {
            List<(ScenarioTrigger Trigger, string Argument)> snapshot = active.ToList();
            if (snapshot.Count == 0)
                return null;

            lock (Sync)
            {
                return Rules
                    .Where(rule => rule.Enabled && Matches(rule, snapshot))
                    .OrderBy(rule => rule.Priority)
                    .Select(rule => rule.ProfileId)
                    .FirstOrDefault(id => !string.IsNullOrEmpty(id));
            }
        }

        /// <summary>方案名；查不到就退回 Id —— 日志里写「（已删除）」会被读成别的问题。</summary>
        private static string Describe(string profileId)
            => ProfileService.FindById(profileId)?.Name ?? profileId;

        /// <summary>
        /// 把「此刻成立的触发」写成一句人话，只服务于日志。
        ///
        /// 「它为什么自己把方案切了」是这个功能最难自查的问题：日志里只有一句
        /// 「已应用方案 X」，用户对着它没法判断是哪条规则命中的。补上触发源，
        /// 排查时一眼就能对上。
        /// </summary>
        private static string DescribeHit(IEnumerable<(ScenarioTrigger Trigger, string Argument)> active)
        {
            List<string> parts = new();
            foreach ((ScenarioTrigger trigger, string argument) in active)
            {
                parts.Add(trigger switch
                {
                    ScenarioTrigger.ProcessRunning =>
                        string.IsNullOrEmpty(argument) ? "进程" : $"进程 {argument}",
                    ScenarioTrigger.OnBattery => "电池供电",
                    _ => "交流供电"
                });

                // 进程能有几百个，全列出来会把一行日志撑成几 KB —— 而它只是背景信息。
                if (parts.Count >= 4)
                {
                    parts.Add("…");
                    break;
                }
            }

            return parts.Count == 0 ? "无任何触发" : string.Join("、", parts);
        }

        private static bool Matches(ScenarioRule rule,
                                    List<(ScenarioTrigger Trigger, string Argument)> active)
            => active.Any(a =>
                a.Trigger == rule.Trigger &&
                string.Equals(a.Argument, rule.Argument, StringComparison.OrdinalIgnoreCase));

        private static int IndexOfId(string id)
        {
            for (int i = 0; i < Rules.Count; i++)
                if (Rules[i].Id == id)
                    return i;
            return -1;
        }

        private static void Persist()
        {
            try
            {
                JsonFile.Write(FilePath, new ScenarioFile
                {
                    Enabled = Enabled,
                    Rules = Rules.ToList()
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"保存场景规则失败: {ex.Message}");
            }
        }
    }
}
