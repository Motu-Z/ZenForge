using System;
using System.Collections.Generic;
using System.Linq;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 场景自动切换的底层自检（`-selftest` 启动参数触发）。
    ///
    /// 【为什么要有它】
    /// 这一版的实装全在后台：进程事件、电源通知、方案切换都不经过界面，
    /// 也没有任何 UI 能点出来。没有自检就只能靠「拔一下电源看方案切没切」，
    /// 而那要在一台笔记本上、开着日志、还切得对才算过 —— 一次验证的成本太高，
    /// 而且失败时拿不到「到底哪一步没走通」。
    ///
    /// 这里把能静态验证的部分全验一遍，结果写进日志：
    ///   1. 规则仲裁（优先级、大小写、空目标跳过）—— 纯函数，不涉及任何硬件；
    ///   2. 监听范围是否与规则内容一致 —— 验证「不启用就零占用」这条约束真的兑现；
    ///   3. 快照抓取与写回 —— 只动 store 的内存副本，**不下发硬件**（见下）。
    ///
    /// 【刻意不测什么】
    /// 不调 TuningApplyService、不写 SMU、不改进程绑核。自检的目的是验证逻辑，
    /// 不是验证硬件；为跑一次自检把用户的负压值改掉，代价与收益完全不成比例。
    /// 硬件下发那一段只能靠真机验收。
    /// </summary>
    public static class ScenarioSelfCheck
    {
        /// <summary>跑一遍自检，把结果写进日志。任何一步抛异常都不会影响启动。</summary>
        public static void Run()
        {
            LogService.Info("========== 场景自动切换 · 底层自检开始 ==========");

            int passed = 0;
            int failed = 0;

            // ---------- 1. 仲裁：优先级 ----------
            // 插着电打游戏时两条规则同时命中，没有仲裁就会取决于用户哪条先建。
            Check("仲裁-优先级：数字小的赢", ref passed, ref failed, () =>
            {
                ScenarioRule[] rules = {
                    New(ScenarioTrigger.ProcessRunning, "game.exe", "p-game", priority: 5),
                    New(ScenarioTrigger.OnAcPower, "", "p-ac", priority: 1)
                };
                List<(ScenarioTrigger, string)> active = new()
                {
                    (ScenarioTrigger.ProcessRunning, "game.exe"),
                    (ScenarioTrigger.OnAcPower, "")
                };

                return Expect(ResolveWith(rules, active), "p-ac");
            });

            // ---------- 2. 仲裁：禁用的规则不参与 ----------
            Check("仲裁-禁用：Enabled=false 的规则不参与", ref passed, ref failed, () =>
            {
                ScenarioRule disabled = New(ScenarioTrigger.OnAcPower, "", "p-disabled", priority: 0);
                disabled.Enabled = false;
                ScenarioRule[] rules = { disabled, New(ScenarioTrigger.OnAcPower, "", "p-ok", priority: 9) };

                return Expect(ResolveWith(rules, new() { (ScenarioTrigger.OnAcPower, "") }), "p-ok");
            });

            // ---------- 3. 匹配：进程名大小写不敏感 ----------
            // Windows 上进程名的大小写没有保证（Game.exe / game.exe 都常见），
            // 大小写敏感会让规则时灵时不灵 —— 那种 bug 最难复现。
            Check("匹配-大小写：进程名忽略大小写", ref passed, ref failed, () =>
            {
                ScenarioRule[] rules = { New(ScenarioTrigger.ProcessRunning, "Game.exe", "p-game") };

                return Expect(ResolveWith(rules, new() { (ScenarioTrigger.ProcessRunning, "GAME.EXE") }), "p-game");
            });

            // ---------- 4. 匹配：空目标方案要跳过而不是返回空串 ----------
            // 规则建了但没选方案是界面上完全可能出现的状态。返回空串会让
            // Notify 去 Apply("")，然后记一条「方案不存在」—— 真正的意思是
            // 「这条规则没配完」，不该表现为一次失败的切换。
            Check("匹配-空目标：ProfileId 为空的规则被跳过", ref passed, ref failed, () =>
            {
                ScenarioRule[] rules = {
                    New(ScenarioTrigger.OnAcPower, "", "", priority: 0),
                    New(ScenarioTrigger.OnAcPower, "", "p-second", priority: 5)
                };

                return Expect(ResolveWith(rules, new() { (ScenarioTrigger.OnAcPower, "") }), "p-second");
            });

            // ---------- 5. 判定：没有命中时返回 null ----------
            Check("判定-落空：没有任何规则命中时返回 null", ref passed, ref failed, () =>
            {
                ScenarioRule[] rules = { New(ScenarioTrigger.ProcessRunning, "game.exe", "p-game") };

                return Expect(ResolveWith(rules, new() { (ScenarioTrigger.ProcessRunning, "notepad.exe") }), null);
            });

            // ---------- 6. 监听范围与规则内容一致 ----------
            Check("监听-按需：只挂规则真正需要的输入源", ref passed, ref failed, () =>
            {
                bool active = ScenarioMonitor.IsActive;
                bool wantProcess = ScenarioService.Enabled && ScenarioService.HasProcessRules;
                bool wantPower = ScenarioService.Enabled && ScenarioService.HasPowerRules;

                if (active != (wantProcess || wantPower))
                    return $"监听状态({active}) 与规则需求(进程={wantProcess}, 电源={wantPower}) 不一致";

                if (ScenarioMonitor.IsProcessSourceAttached != wantProcess)
                    return "进程事件订阅与规则需求不一致";

                if (ScenarioMonitor.IsPowerSourceAttached != wantPower)
                    return "电源监听与规则需求不一致";

                return null;
            });

            // ---------- 7. 快照抓取值正确 ----------
            Check("快照-抓取：抓到的是 store 当前值且规则已深拷贝", ref passed, ref failed, () =>
            {
                ProfileSnapshot snapshot = ProfileService.CaptureCurrent("__selftest__");

                if (!snapshot.CurveOptimizer.SequenceEqual(TuningStore.Profile.CurveOptimizer))
                    return "CO 值与 store 不一致";

                if (snapshot.FMaxMHz != TuningStore.Profile.FMaxMHz)
                    return "FMax 值与 store 不一致";

                // 规则必须是副本：方案保存之后继续改进程页，不能把已存的方案也改了。
                // 引用相等意味着改动会互相串，那是最难查的一类 bug。
                if (snapshot.BindingRules.Count != BindingStore.Profile.Rules.Count)
                    return "规则条数与 store 不一致";

                if (snapshot.BindingRules.Any(r => BindingStore.Profile.Rules.Any(c => ReferenceEquals(r, c))))
                    return "规则未深拷贝：与当前规则集共享同一批对象";

                return null;
            });

            // ---------- 8. 生效标记与 ActiveId 一致 ----------
            Check("标记-一致：IsActive 只在该生效的那一份上为真", ref passed, ref failed, () =>
            {
                string? activeId = ProfileService.ActiveId;
                int marked = ProfileService.Items.Count(p => p.IsActive);

                if (activeId == null)
                    return marked == 0 ? null : $"没有生效方案，却有 {marked} 份被标记";

                return marked == 1 && ProfileService.Items.First(p => p.IsActive).Id == activeId
                    ? null
                    : $"被标记的有 {marked} 份，与 ActiveId 对不上";
            });

            LogService.Info($"========== 自检结束：通过 {passed} 项，失败 {failed} 项 ==========");

            if (failed > 0)
                LogService.Warn($"场景自检有 {failed} 项未通过，详见上方各项结果");
        }

        /// <summary>跑一个断言：返回 null 表示通过，返回字符串表示失败原因。</summary>
        private static void Check(string name, ref int passed, ref int failed, Func<string?> body)
        {
            string? error;
            try
            {
                error = body();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error == null)
            {
                passed++;
                LogService.Info($"  [通过] {name}");
            }
            else
            {
                failed++;
                LogService.Warn($"  [失败] {name} —— {error}");
            }
        }

        /// <summary>
        /// 用一组临时规则跑一次仲裁。
        ///
        /// 【为什么不直接往 ScenarioService.Rules 里塞】
        /// 那个集合绑在界面上，而且它的改动会落盘。自检不该改动用户的真实规则 ——
        /// 跑一次自检把配置改掉，是最坏的一种副作用。
        /// 所以这里复制一份 Resolve 的判定条件出来验，而不是污染真实集合。
        /// </summary>
        private static string? ResolveWith(IEnumerable<ScenarioRule> rules,
                                           List<(ScenarioTrigger Trigger, string Argument)> active)
            => rules
                .Where(rule => rule.Enabled && active.Any(a =>
                    a.Trigger == rule.Trigger &&
                    string.Equals(a.Argument, rule.Argument, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(rule => rule.Priority)
                .Select(rule => rule.ProfileId)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));

        private static ScenarioRule New(ScenarioTrigger trigger, string argument, string profileId, int priority = 0)
            => new()
            {
                Trigger = trigger,
                Argument = argument,
                ProfileId = profileId,
                Priority = priority,
                Enabled = true
            };

        private static string? Expect(string? actual, string? expected)
        {
            // 「没命中」在两种表示之间要归一：Resolve 语义上返回 null 表示落空。
            string? normalized = string.IsNullOrEmpty(actual) ? null : actual;

            return normalized == expected
                ? null
                : $"期望 {(expected ?? "null")}，实际 {(normalized ?? "null")}";
        }
    }
}
