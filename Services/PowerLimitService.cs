using System;
using System.Collections.Generic;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 功耗墙的四项写入与恢复。
    ///
    /// 三条设计约束（都是从 UXTU 的做法里反推出来的，不是拍脑袋）：
    ///
    ///   1. 逐项独立。任何一项失败都不中断其它项 ——
    ///      实测里「PPT 可写、TDC 被固件锁死」的机型很常见，全有或全无会导致
    ///      用户连能调的那项都调不了。失败项的原始 Status 全部保留，界面逐项展示。
    ///
    ///   2. 写入顺序固定：PPT → TDC → EDC → Tctl。
    ///      反过来先收紧温度墙的话，后续功耗墙的写入会在热保护下被 SMU 反复拒绝。
    ///
    ///   3. 不自动开 OC 模式。UXTU 遇到 CMD_REJECTED_PREREQ 会先发 enable-oc 绕过，
    ///      但那会把 BIOS 里的「手动超频」标志置起，属于副作用外溢。
    ///      这里的做法是：识别前置条件失败，原样上报，由用户自己去 BIOS 开 PBO。
    /// </summary>
    public static class PowerLimitService
    {
        private static readonly object Sync = new();

        /// <summary>固定写入顺序，见类注释第 2 条。</summary>
        private static readonly PowerLimitKind[] WriteOrder =
        {
            PowerLimitKind.Ppt,
            PowerLimitKind.Tdc,
            PowerLimitKind.Edc,
            PowerLimitKind.Tctl
        };

        /// <summary>
        /// 逐项写入。不抛异常，任何失败都体现在返回值的 Items 里。
        /// </summary>
        public static PowerLimitApplyResult Apply(SmuReader reader, PowerLimitSet set)
        {
            PowerLimitApplyResult result = new();
            if (reader == null || set == null || set.IsEmpty)
                return result;

            Dictionary<PowerLimitKind, uint> map = set.ToDictionary();

            lock (Sync)
            {
                foreach (PowerLimitKind kind in WriteOrder)
                {
                    if (!map.TryGetValue(kind, out uint value))
                        continue;

                    PowerLimitOutcome outcome = WriteOne(reader, kind, value);
                    result.Items.Add(outcome);

                    LogOutcome(outcome);
                }
            }

            return result;
        }

        /// <summary>
        /// 恢复出厂值。只恢复基线里有值的项。
        ///
        /// EDC 永远不在恢复范围内：融断值结构里根本没有 EDC 通道，
        /// 任何一个「EDC 出厂值」都只能是从当前生效值反推的，那不是出厂值。
        /// 界面上该项的「恢复默认」按钮直接置灰，这里也不做兜底猜测。
        /// </summary>
        public static PowerLimitApplyResult RestoreDefaults(SmuReader reader, PowerBaseline? baseline)
        {
            PowerLimitApplyResult result = new();
            if (reader == null)
                return result;

            if (baseline == null || !baseline.IsUsable)
            {
                LogService.Warn("恢复默认跳过：没有可用的出厂基线");
                return result;
            }

            PowerLimitSet set = new();
            if (baseline.HasPpt) set.PptMilliwatt = (uint)baseline.PptMilliwatt;
            if (baseline.HasTdc) set.TdcMilliamp = (uint)baseline.TdcMilliamp;
            if (baseline.HasTctl) set.TctlCelsius = (uint)baseline.TctlCelsius;

            LogService.Info(
                $"恢复出厂功耗墙：PPT {baseline.PptMilliwatt} mW / " +
                $"TDC {baseline.TdcMilliamp} mA / Tctl {baseline.TctlCelsius} ℃（EDC 无出厂值，不写入）");

            return Apply(reader, set);
        }

        private static PowerLimitOutcome WriteOne(SmuReader reader, PowerLimitKind kind, uint value)
        {
            string name = PowerLimitOutcome.NameOf(kind);

            // 写入前现读一次。PPT / TDC / EDC 没有读取接口，这里只会拿到 0；
            // Tctl 拿到的是系统供电策略里的温度上限。仅用于日志与撤销，不作为恢复依据。
            uint before = reader.ReadPowerLimit(kind, out _);

            ZenStates.Core.SMU.Status status;
            try
            {
                status = reader.SetPowerLimit(kind, value);
            }
            catch (Exception ex)
            {
                LogService.Error($"{name} 写入抛出异常：{ex}");
                return new PowerLimitOutcome
                {
                    Kind = kind,
                    Requested = value,
                    Before = before,
                    Message = $"写入异常：{ex.Message}"
                };
            }

            bool unsupported = status == ZenStates.Core.SMU.Status.UNKNOWN_CMD;
            bool prereq = status == ZenStates.Core.SMU.Status.CMD_REJECTED_PREREQ;

            // 回读校验：只有 Tctl 有可信通道。0 表示通道不可用，不参与判定。
            uint readBack = reader.ReadPowerLimit(kind, out _);
            bool verified = true;
            if (readBack > 0 && kind == PowerLimitKind.Tctl)
            {
                uint diff = readBack > value ? readBack - value : value - readBack;
                verified = diff <= PowerLimitBounds.ReadBackTolerance;
            }

            bool ok = status == ZenStates.Core.SMU.Status.OK && verified;

            return new PowerLimitOutcome
            {
                Kind = kind,
                Ok = ok,
                RawStatus = status,
                Requested = value,
                ReadBack = readBack,
                Before = before,
                Unsupported = unsupported,
                BlockedByPrereq = prereq,
                Message = ok
                    ? (readBack > 0
                        ? $"已写入，回读 {Format(kind, readBack)}"
                        : "已写入（该平台无回读通道）")
                    : Describe(status, kind, value, readBack, verified)
            };
        }

        private static string Describe(
            ZenStates.Core.SMU.Status status, PowerLimitKind kind, uint value, uint readBack, bool verified)
        {
            string name = PowerLimitOutcome.NameOf(kind);

            if (status == ZenStates.Core.SMU.Status.UNKNOWN_CMD)
                return "该平台不支持此项";

            if (status == ZenStates.Core.SMU.Status.CMD_REJECTED_PREREQ)
                return "需在 BIOS 开启 PBO";

            if (!verified)
                return $"回读 {Format(kind, readBack)}，与目标不符";

            return status switch
            {
                ZenStates.Core.SMU.Status.CMD_REJECTED_BUSY => "SMU 忙，稍后重试",
                ZenStates.Core.SMU.Status.TIMEOUT_MUTEX_LOCK => "总线互斥超时",
                ZenStates.Core.SMU.Status.TIMEOUT_MAILBOX_READY => "邮箱未就绪超时",
                ZenStates.Core.SMU.Status.TIMEOUT_MAILBOX_MSG_WRITE => "邮箱写入超时",
                ZenStates.Core.SMU.Status.PCI_FAILED => "PCI 访问失败",
                ZenStates.Core.SMU.Status.FAILED => "SMU 拒绝写入",
                _ => $"{name} 写入失败（{status}）"
            };
        }

        /// <summary>把内部单位换算成人读的形式，只用于日志与提示。</summary>
        public static string Format(PowerLimitKind kind, uint raw) => kind switch
        {
            PowerLimitKind.Ppt => $"{raw / 1000.0:0.#} W",
            PowerLimitKind.Tdc => $"{raw / 1000.0:0.#} A",
            PowerLimitKind.Edc => $"{raw / 1000.0:0.#} A",
            PowerLimitKind.Tctl => $"{raw} ℃",
            _ => raw.ToString()
        };

        private static void LogOutcome(PowerLimitOutcome outcome)
        {
            string name = PowerLimitOutcome.NameOf(outcome.Kind);
            string text = $"功耗墙 {name} = {Format(outcome.Kind, outcome.Requested)}（{outcome.Requested} 原始单位）";

            if (outcome.Ok)
                LogService.Info($"{text} · {outcome.Message}");
            else if (outcome.BlockedByPrereq)
                LogService.Warn($"{text} · 前置条件不满足：需在 BIOS 开启 PBO");
            else if (outcome.Unsupported)
                LogService.Warn($"{text} · 该平台不支持此项，已跳过");
            else
                LogService.Error($"{text} · {outcome.Message}");
        }
    }
}
