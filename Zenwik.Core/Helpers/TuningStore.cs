using System;
using System.IO;
using Zenwik.Core.Services;

namespace Zenwik.Core.Helpers
{
    /// <summary>
    /// 调校配置（Curve Optimizer 每核负压 + FMax），持久化到 %APPDATA%\Zenwik.Core\tuning.json。
    /// 仅保存“已成功下发并回读校验通过”的设定，供开机静默自启重新应用。
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

        /// <summary>最近一次成功应用的时间（本地时间，ISO8601）。</summary>
        public string? AppliedAt { get; set; }
    }

    public static class TuningStore
    {
        private static readonly string FilePath = AppPaths.File("tuning.json");

        public static TuningProfile Profile { get; private set; } = new();

        public static void Load()
        {
            try
            {
                if (JsonFile.Read<TuningProfile>(FilePath) is { } loaded)
                    Profile = loaded;
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
                JsonFile.Write(FilePath, profile);
                Profile = profile;
            }
            catch (Exception ex)
            {
                LogService.Error($"保存调校配置失败: {ex.Message}");
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

            Save(new TuningProfile
            {
                CurveOptimizer = Array.Empty<int>(),
                FMaxMHz = baseline,
                FMaxBaselineMHz = baseline
            });

            if (baseline == 0)
                LogService.Info("全局重置：CO 归零；FMax 出厂基线未知，未写回 FMax");
            else
                LogService.Info($"全局重置：CO 归零，FMax 恢复出厂 {baseline} MHz");

            return baseline;
        }
    }
}
