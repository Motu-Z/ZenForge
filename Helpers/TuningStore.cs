using System;
using System.IO;
using System.Text.Json;
using ZenForge.Services;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 调校配置（Curve Optimizer 每核负压 + FMax），持久化到 %APPDATA%\ZenForge\tuning.json。
    /// 仅保存“已成功下发并回读校验通过”的设定，供开机静默自启（S7）重新应用。
    /// </summary>
    public sealed class TuningProfile
    {
        /// <summary>每核 CO 值（长度 = 物理核心数，范围 [-50, +50]，0 = 不偏移；负降压 / 正加压）。</summary>
        public int[] CurveOptimizer { get; set; } = Array.Empty<int>();

        /// <summary>FMax 上限（MHz）。0 = 未设置。</summary>
        public uint FMaxMHz { get; set; }

        /// <summary>
        /// FMax 出厂基线（MHz）。首次从 SMU 读到就存盘；读不到回落 5400。
        /// 全局重置时 FMax 恢复到此基线（或 5400）。
        /// </summary>
        public uint FMaxBaselineMHz { get; set; }

        // ---------- 功耗墙（0 = 未设置） ----------
        //
        // 单位沿用 SMU 原始单位，不存瓦和安：
        //   · 功率 mW、电流 mA、温度 ℃。
        // 界面换算集中在 TuningPage 的边界处，配置文件里出现两种单位是自找麻烦。

        /// <summary>PPT 功耗上限，mW。</summary>
        public uint PptMilliwatt { get; set; }

        /// <summary>TDC 持续电流上限，mA。</summary>
        public uint TdcMilliamp { get; set; }

        /// <summary>EDC 峰值电流上限，mA。</summary>
        public uint EdcMilliamp { get; set; }

        /// <summary>Tctl 温度上限，℃。</summary>
        public uint TctlCelsius { get; set; }

        /// <summary>是否保存过任何一项功耗墙。</summary>
        public bool HasPowerLimits =>
            PptMilliwatt > 0 || TdcMilliamp > 0 || EdcMilliamp > 0 || TctlCelsius > 0;

        /// <summary>最近一次成功应用的时间（本地时间，ISO8601）。</summary>
        public string? AppliedAt { get; set; }
    }

    public static class TuningStore
    {
        private static readonly string Directory =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZenForge");

        private static readonly string FilePath = System.IO.Path.Combine(Directory, "tuning.json");

        public static TuningProfile Profile { get; private set; } = new();

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    TuningProfile? loaded = JsonSerializer.Deserialize<TuningProfile>(File.ReadAllText(FilePath));
                    if (loaded != null)
                        Profile = loaded;
                }
            }
            catch
            {
                Profile = new TuningProfile();
            }
        }

        /// <summary>用给定设定覆盖当前 Profile 并落盘（先写临时文件再原子替换）。</summary>
        public static void Save(TuningProfile profile)
        {
            profile.AppliedAt = DateTime.Now.ToString("o");
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, true);
                Profile = profile;
            }
            catch (Exception ex)
            {
                // 配置落盘失败不影响主流程，但记录日志
                Services.LogService.Error($"保存调校配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 全局重置为出厂值：CO 归零（CurveOptimizer 置空）、FMax 恢复出厂基线
        /// （<see cref="TuningProfile.FMaxBaselineMHz"/>）。返回实际生效的 FMax（供调用方写回 SMU）；
        /// 返回 0 表示基线未知，调用方应跳过 FMax 写回，而不是拿一个猜来的值去写。
        ///
        /// 早先基线未知时回落 5400——那是桌面 Zen5 的值，在移动端或其他 SKU 上
        /// 要么超出真实上限，要么把频率锁在一个陌生数字上。"不知道"就该说不知道。
        /// </summary>
        public static uint ResetToFactory()
        {
            uint baseline = Profile.FMaxBaselineMHz;
            uint effectiveFMax = baseline != 0 ? baseline : 0u;

            // 功耗墙不在配置里清零：它的「出厂值」存在 power-baseline.json，
            // 由 PowerLimitService.RestoreDefaults 负责写回 SMU，
            // 这里只把「用户设定的墙值」抹掉，避免下次自启又把自定义值写回去。
            TuningProfile reset = new()
            {
                CurveOptimizer = Array.Empty<int>(),
                FMaxMHz = effectiveFMax,
                FMaxBaselineMHz = baseline
            };
            Save(reset);

            if (effectiveFMax == 0)
                LogService.Info("全局重置：CO 归零；FMax 出厂基线未知，未写回 FMax");
            else
                LogService.Info($"全局重置：CO 归零，FMax 恢复出厂 {effectiveFMax} MHz");

            return effectiveFMax;
        }
    }
}
