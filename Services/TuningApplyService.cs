using System;
using System.Collections.Generic;
using System.Linq;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>把已保存的调校写回 SMU 的结果。</summary>
    public sealed class TuningApplyOutcome
    {
        /// <summary>没有执行写入（开关关闭，或根本没有保存过配置）。</summary>
        public bool Skipped { get; init; }

        /// <summary>跳过的原因。Skipped 为 true 时有值。</summary>
        public string SkipReason { get; init; } = string.Empty;

        public bool CurveOptimizerApplied { get; set; }
        public bool FMaxApplied { get; set; }

        /// <summary>执行了写入且没有任何失败项。</summary>
        public bool AnythingApplied => CurveOptimizerApplied || FMaxApplied;

        public bool Success => !Skipped && Errors.Count == 0 && AnythingApplied;

        public List<string> Errors { get; } = new();

        /// <summary>一句话摘要，用于界面状态行。</summary>
        public string Summary => Skipped
            ? SkipReason
            : (Errors.Count == 0
                ? string.Empty
                : string.Join("；", Errors));
    }

    /// <summary>
    /// 「把上次保存的调校重新写进 SMU」这一件事的唯一实现。
    ///
    /// 以前这段逻辑只长在 <see cref="SilentModeRunner"/> 里，也就是只有
    /// 开机计划任务拉起的 <c>-silent</c> 进程会执行。用户双击图标正常启动 GUI 时
    /// 走到的是另一条分支，只 Load 不写入 —— 于是设置页里的
    /// 「启动时自动应用调校」开关在最常见的启动方式下完全不起作用。
    /// 两条路径现在共用本类。
    /// </summary>
    public static class TuningApplyService
    {
        /// <summary>最近一次执行结果，供调校页在加载后显示。</summary>
        public static TuningApplyOutcome? LastOutcome { get; private set; }

        /// <summary>
        /// 读取 tuning.json 并写回 SMU。全程可抛异常已内部兜住，返回值里带失败明细。
        /// 调用方决定要不要弹窗（静默模式不弹，GUI 模式弹）。
        /// </summary>
        public static TuningApplyOutcome ApplyFromStore()
        {
            TuningStore.Load();
            UiSettingsStore.Load();

            TuningProfile profile = TuningStore.Profile;
            int[] margins = profile.CurveOptimizer ?? Array.Empty<int>();
            uint fMax = profile.FMaxMHz;

            bool hasCo = margins.Length > 0 && margins.Any(v => v != int.MinValue);
            bool hasFMax = fMax > 0;

            // 跳过也算一次结果：调校页要靠它显示「本次启动未应用 + 原因」，
            // 否则用户无从区别「应用失败」和「压根没尝试」。
            if (!UiSettingsStore.Settings.ApplyTuningOnStartup)
                return Record(new TuningApplyOutcome
                {
                    Skipped = true,
                    SkipReason = "「启动时自动应用调校」已关闭，本次未写入 SMU"
                });

            if (!hasCo && !hasFMax)
                return Record(new TuningApplyOutcome
                {
                    Skipped = true,
                    SkipReason = "没有已保存的调校，本次未写入 SMU"
                });

            TuningApplyOutcome outcome = new();
            Write(profile, margins, fMax, hasCo, hasFMax, outcome);
            return Record(outcome);
        }

        private static TuningApplyOutcome Record(TuningApplyOutcome outcome)
        {
            LastOutcome = outcome;
            return outcome;
        }

        /// <summary>执行 SMU 写入（CO / FMax）。失败明细写进 outcome，不向外抛。</summary>
        private static void Write(
            TuningProfile profile,
            int[] margins,
            uint fMax,
            bool hasCo,
            bool hasFMax,
            TuningApplyOutcome outcome)
        {
            // WaitForPawnIO 返回的是进程共享的同一个 SmuReader（内部只创建一个 Cpu），
            // 驱动就绪即返回可用实例；超时（60s）仍不可用则跳过，不弹窗、不卡死。
            // 本方法只在后台线程被调用，等 60 秒不会冻住界面。
            SmuReader? reader = SmuReader.WaitForPawnIO(60);
            if (reader == null)
            {
                outcome.Errors.Add("PawnIO 未加载（60 秒内未就绪），跳过调校写入");
                LogService.Error("启动应用调校：PawnIO 未加载，跳过写入");
                return;
            }

            try
            {
                LogService.Info(
                    $"启动应用调校：读取到已保存配置，CO {margins.Length} 核" +
                    (hasFMax ? $"，FMax {fMax} MHz" : "，未设置 FMax"));

                // ReadSnapshot 会填充写入所需的拓扑（核心数 / 每 CCD 核数）
                try
                {
                    SmuSnapshot snapshot = reader.ReadSnapshot();
                    if (!snapshot.Success)
                        LogService.Error(
                            $"启动应用调校：SMU 探测失败（{snapshot.Error}），继续尝试写入");
                }
                catch (Exception ex)
                {
                    LogService.Warn($"启动应用调校：SMU 探测异常（继续尝试写入）：{ex.Message}");
                }

                // ---------- CO ----------
                if (hasCo)
                {
                    // 补齐到实际核心数：配置文件可能来自核心数不同的机器
                    int coreCount = reader.CoreCount > 0 ? reader.CoreCount : margins.Length;
                    int[] values = new int[coreCount];
                    for (int i = 0; i < coreCount; i++)
                        values[i] = i < margins.Length ? margins[i] : 0;

                    try
                    {
                        SmuWriteResult co = reader.WriteCurveOptimizer(values);

                        if (co.AllSucceeded)
                        {
                            outcome.CurveOptimizerApplied = true;
                            LogService.Info($"启动应用调校：CO {co.Applied.Count} 核写入并回读一致");
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(co.Fatal))
                                outcome.Errors.Add($"CO 写入致命错误：{co.Fatal}");
                            if (co.WriteFailed.Count > 0)
                                outcome.Errors.Add($"CO 被拒核心：{string.Join(", ", co.WriteFailed)}");
                            if (co.VerifyFailed.Count > 0)
                                outcome.Errors.Add($"CO 回读不一致核心：{string.Join(", ", co.VerifyFailed)}");

                            LogService.Error("启动应用调校：CO 写入未完全成功（详见上方明细）");
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome.Errors.Add($"CO 写入异常：{ex.Message}");
                        LogService.Error($"启动应用调校：CO 写入异常：{ex.Message}");
                    }
                }

                // ---------- FMax ----------
                if (hasFMax)
                {
                    try
                    {
                        SmuFMaxResult fm = reader.WriteFMax(fMax);
                        if (fm.AllSucceeded)
                        {
                            outcome.FMaxApplied = true;
                            LogService.Info($"启动应用调校：FMax {fm.ReadBack} MHz 写入并回读一致");
                        }
                        else
                        {
                            outcome.Errors.Add(fm.Fatal != null
                                ? $"FMax 写入失败：{fm.Fatal}"
                                : $"FMax 回读不一致（请求 {fm.Requested}，回读 {fm.ReadBack}）");

                            LogService.Error(
                                fm.Fatal != null
                                    ? $"启动应用调校：FMax 写入失败：{fm.Fatal}"
                                    : $"启动应用调校：FMax 回读不一致（请求 {fm.Requested}，回读 {fm.ReadBack}）");
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome.Errors.Add($"FMax 写入异常：{ex.Message}");
                        LogService.Error($"启动应用调校：FMax 写入异常：{ex.Message}");
                    }
                }
            }
            finally
            {
                // 不 Dispose：reader 是进程共享单例，而 Cpu.Dispose() 会
                // Opcode.Close() 掉进程级静态资源，一键打哑所有 Cpu 的 SMU 通道。
                // SmuReader.Dispose 本身也已是 no-op，这里只是把意图写明。
            }
        }
    }
}
