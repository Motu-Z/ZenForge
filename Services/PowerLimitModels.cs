using System;
using System.Collections.Generic;

namespace ZenForge.Services
{
    /// <summary>四项功耗墙。写入顺序即枚举顺序：先放宽功耗与电流，最后收温度。</summary>
    public enum PowerLimitKind
    {
        Ppt,   // 封装功耗上限，mW
        Tdc,   // 持续电流上限（VDD），mA
        Edc,   // 峰值电流上限（VDD），mA
        Tctl   // Tctl 温度上限，℃
    }

    /// <summary>
    /// 单项墙的写入结果。
    ///
    /// 失败原因按 SMU 的 Status 分三层处理，不合并成一句「写入失败」：
    ///   · UNKNOWN_CMD        —— 命令本身不存在，重试无意义，该项永久不可用；
    ///   · CMD_REJECTED_PREREQ —— 前置条件不满足（BIOS 未开 PBO / OC 模式），
    ///                            需要明确告诉用户去 BIOS 改，而不是反复重试；
    ///   · 其余（超时 / 总线 / 拒绝）—— 瞬时故障，可重试。
    /// 三层混在一起时，用户只能看到一个「失败」，既不知道能不能重试，也不知道该改什么。
    /// </summary>
    public sealed class PowerLimitOutcome
    {
        public PowerLimitKind Kind { get; init; }

        /// <summary>写入并回读一致。</summary>
        public bool Ok { get; init; }

        /// <summary>库返回的原始状态。异常路径为 null。</summary>
        public object? RawStatus { get; init; }

        /// <summary>发出去的值（mW / mA / ℃）。</summary>
        public uint Requested { get; init; }

        /// <summary>写入后回读值；0 表示该平台没有回读通道。</summary>
        public uint ReadBack { get; init; }

        /// <summary>写入前实测值，仅供日志与「撤销本次应用」，不是出厂值。</summary>
        public uint Before { get; init; }

        /// <summary>给用户看的短句。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>命令不存在。重试无意义。</summary>
        public bool Unsupported { get; init; }

        /// <summary>前置条件不满足，需要用户去 BIOS 处理。</summary>
        public bool BlockedByPrereq { get; init; }

        /// <summary>中文显示名，日志与界面共用。</summary>
        public static string NameOf(PowerLimitKind kind) => kind switch
        {
            PowerLimitKind.Ppt => "PPT",
            PowerLimitKind.Tdc => "TDC",
            PowerLimitKind.Edc => "EDC",
            PowerLimitKind.Tctl => "Tctl",
            _ => kind.ToString()
        };

        /// <summary>单位符号。</summary>
        public static string UnitOf(PowerLimitKind kind) => kind switch
        {
            PowerLimitKind.Ppt => "W",
            PowerLimitKind.Tdc => "A",
            PowerLimitKind.Edc => "A",
            PowerLimitKind.Tctl => "℃",
            _ => string.Empty
        };
    }

    /// <summary>
    /// 一次「应用」的完整结果。逐项独立，任一项失败不影响其它项，
    /// 但每一项的成败与原因都保留在这里，由调用方决定怎么展示。
    /// </summary>
    public sealed class PowerLimitApplyResult
    {
        public List<PowerLimitOutcome> Items { get; } = new();

        public bool AnySucceeded => Items.Exists(i => i.Ok);
        public bool AllSucceeded => Items.TrueForAll(i => i.Ok);

        /// <summary>有项被 BIOS 前置条件挡住。界面据此标红提示开启 PBO。</summary>
        public bool HasPrereqBlock => Items.Exists(i => i.BlockedByPrereq);

        /// <summary>成功写入并留下快照的项，供「撤销本次应用」使用。</summary>
        public List<PowerLimitOutcome> Applied => Items.FindAll(i => i.Ok);
    }

    /// <summary>
    /// 一组墙值。单位统一为库与 SMU 使用的原始单位：
    /// 功率 mW、电流 mA、温度 ℃。界面上的 W / A 在边界处换算，内部不出现瓦和安。
    /// </summary>
    public sealed class PowerLimitSet
    {
        public uint? PptMilliwatt { get; set; }
        public uint? TdcMilliamp { get; set; }
        public uint? EdcMilliamp { get; set; }
        public uint? TctlCelsius { get; set; }

        public bool IsEmpty =>
            PptMilliwatt == null && TdcMilliamp == null && EdcMilliamp == null && TctlCelsius == null;

        public Dictionary<PowerLimitKind, uint> ToDictionary()
        {
            Dictionary<PowerLimitKind, uint> map = new();
            if (PptMilliwatt.HasValue) map[PowerLimitKind.Ppt] = PptMilliwatt.Value;
            if (TdcMilliamp.HasValue) map[PowerLimitKind.Tdc] = TdcMilliamp.Value;
            if (EdcMilliamp.HasValue) map[PowerLimitKind.Edc] = EdcMilliamp.Value;
            if (TctlCelsius.HasValue) map[PowerLimitKind.Tctl] = TctlCelsius.Value;
            return map;
        }
    }

    /// <summary>输入合法区间。只拦明显不可能的数值，不做安全限制。</summary>
    public static class PowerLimitBounds
    {
        /// <summary>PPT：5 W ~ 500 W。</summary>
        public const int PptMinWatt = 5;
        public const int PptMaxWatt = 500;

        /// <summary>TDC / EDC：1 A ~ 500 A。</summary>
        public const int CurrentMinAmp = 1;
        public const int CurrentMaxAmp = 500;

        /// <summary>温度墙：40 ℃ ~ 115 ℃。</summary>
        public const int TctlMinCelsius = 40;
        public const int TctlMaxCelsius = 115;

        /// <summary>回读比对容差。温度墙的出厂通道与写入通道不同源，必须放宽。</summary>
        public const uint ReadBackTolerance = 2;
    }
}
