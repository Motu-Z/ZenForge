using System;
using System.Collections.Generic;
using System.Linq;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>静默模式执行结果（用于日志与退出码判断）。</summary>
    public sealed class SilentRunResult
    {
        /// <summary>是否成功应用了 CO。</summary>
        public bool CurveOptimizerApplied { get; set; }

        /// <summary>是否成功应用了 FMax。</summary>
        public bool FMaxApplied { get; set; }

        /// <summary>是否有任何一项被应用（全部跳过时也视为成功退出）。</summary>
        public bool AnythingApplied => CurveOptimizerApplied || FMaxApplied;

        /// <summary>失败原因汇总（成功时为空）。</summary>
        public List<string> Errors { get; } = new();

        /// <summary>是否启动了进程绑核监听。</summary>
        public bool WatcherStarted { get; set; }

        /// <summary>是否存在启用中的绑核规则（决定这份常驻是否还有意义）。</summary>
        public bool HasActiveRules { get; set; }
    }

    /// <summary>
    /// 静默应用配置（-silent 模式）。
    ///
    /// 触发场景：计划任务在用户登录 15 秒后以最高权限启动
    ///   ZenForge.exe -silent
    ///
    /// 执行顺序：
    ///   1. 读取 tuning.json（TuningStore）
    ///   2. 探测 SMU（ReadSnapshot 同时填充核心数 / 每 CCD 核数等写入所需拓扑）
    ///   3. 写入 CO（WriteCurveOptimizer）与 FMax（WriteFMax），各自回读校验
    ///   4. 若「进程绑定后台监听」开启 → 启动 ProcessWatcher
    ///   5. 全程无窗口、不弹任何对话框，仅写日志
    ///
    /// 与 UI 路径的关键差异：
    ///   · 不调用 DarkDialog / MessageBox —— 静默启动时用户可能还没登录桌面，
    ///     一旦弹窗会卡住进程并留下僵尸窗口。
    ///   · 写入失败只记日志，不中断后续步骤（例如 CO 失败时仍尝试 FMax）。
    /// </summary>
    public static class SilentModeRunner
    {
        /// <summary>执行静默应用。返回结果供 App 决定退出码。</summary>
        public static SilentRunResult Run()
        {
            SilentRunResult result = new();

            LogService.Info("=== 静默模式启动（-silent）===");

            // ---------- 1. 读取配置 ----------
            TuningStore.Load();
            BindingStore.Load();
            UiSettingsStore.Load();

            TuningProfile profile = TuningStore.Profile;
            int[] margins = profile.CurveOptimizer ?? Array.Empty<int>();
            uint fMax = profile.FMaxMHz;

            bool hasCo = margins.Length > 0 && margins.Any(v => v != int.MinValue);
            bool hasFMax = fMax > 0;

            // 受「启动时自动应用调校」开关约束：关闭时只启动监听，不碰 SMU
            bool applyTuning = UiSettingsStore.Settings.ApplyTuningOnStartup;

            if (!applyTuning)
            {
                LogService.Info("「启动时自动应用调校」已关闭，跳过 SMU 写入（仅启动后台监听）");
            }
            else if (!hasCo && !hasFMax)
            {
                LogService.Info("tuning.json 中没有已保存的调校，跳过 SMU 写入");
            }
            else
            {
                LogService.Info(
                    $"读取到已保存调校：CO {margins.Length} 核" +
                    (hasFMax ? $"，FMax {fMax} MHz" : "，未设置 FMax"));
            }

            // ---------- 2. 探测 SMU 并写入 ----------
            // 写入逻辑已抽到 TuningApplyService，与 GUI 正常启动那条路径共用同一份实现，
            // 避免「静默能应用、双击启动不能应用」这种两条路径各自演进的偏差。
            if (applyTuning && (hasCo || hasFMax))
            {
                TuningApplyOutcome outcome = TuningApplyService.ApplyFromStore();
                result.CurveOptimizerApplied = outcome.CurveOptimizerApplied;
                result.FMaxApplied = outcome.FMaxApplied;
                result.Errors.AddRange(outcome.Errors);
            }

            // ---------- 3. 启动后台监听 ----------
            // 两个条件缺一不可：
            //   · 开关开着（用户确实要后台绑核）；
            //   · 有会实际生效的规则（否则监听起来也是空转，白占一份常驻内存）。
            // 只有 CO/FMax 而没有绑核规则时，SMU 写完就已经生效，进程没有常驻的必要。
            result.HasActiveRules = BindingStore.HasEnabledRules;

            if (UiSettingsStore.Settings.WatchProcessBinding && result.HasActiveRules)
            {
                try
                {
                    ProcessWatcher.Start();

                    // Start() 可能因单实例互斥体被占用而跳过，必须以实际状态为准：
                    // 若另一个实例已在监听，本进程不必常驻，直接退出即可。
                    result.WatcherStarted = ProcessWatcher.IsRunning;

                    LogService.Info(result.WatcherStarted
                        ? "静默模式：已启动进程绑核监听"
                        : "静默模式：已有其它实例在监听，本进程无需常驻");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"启动进程绑核监听失败：{ex.Message}");
                    LogService.Error($"静默模式：启动进程绑核监听失败：{ex.Message}");
                }
            }
            else if (!UiSettingsStore.Settings.WatchProcessBinding)
            {
                LogService.Info("静默模式：进程绑定后台监听未开启，跳过");
            }
            else
            {
                LogService.Info("静默模式：没有启用中的绑核规则，跳过常驻");
            }

            // ---------- 4. 汇总 ----------
            if (result.Errors.Count == 0)
            {
                LogService.Info("=== 静默模式完成：全部步骤成功 ===");
            }
            else
            {
                LogService.Warn($"=== 静默模式完成：{result.Errors.Count} 项失败 ===");
                foreach (string error in result.Errors)
                    LogService.Warn($"  · {error}");
            }

            return result;
        }
    }
}
