using System;
using System.IO;
using System.Text.Json;
using ZenForge.Services;

namespace ZenForge.Helpers
{
    /// <summary>
    /// 出厂功耗基线。启动时采集一次落盘，「恢复默认」读它而不是现读。
    ///
    /// 为什么必须落盘：桌面平台的融断值（PboFusedLimits）虽然是出厂定义，
    /// 但一旦用户改过墙值，某些固件会让融断读取返回「当前生效值」而非出厂值。
    /// 每次都现读，等于拿被污染后的值当出厂值，恢复完还是错的。
    ///
    /// 为什么仍要留「现读并覆盖」：换了 BIOS、刷了固件、换过 CPU 之后基线会过期，
    /// 必须有一个显式动作能把它推倒重来。自动重取是不行的 —— 那等于把污染变成常态。
    /// </summary>
    public sealed class PowerBaseline
    {
        public string CpuName { get; set; } = string.Empty;
        public string CodeName { get; set; } = string.Empty;
        public string SmuVersion { get; set; } = string.Empty;

        /// <summary>PPT 出厂值，mW。0 = 该平台未提供。</summary>
        public int PptMilliwatt { get; set; }

        /// <summary>TDC 出厂值（VDD 持续电流），mA。0 = 未提供。</summary>
        public int TdcMilliamp { get; set; }

        /// <summary>EDC 出厂值，mA。恒为 0：融断值里没有独立 EDC 通道，见 PowerLimitService 注释。</summary>
        public int EdcMilliamp { get; set; }

        /// <summary>温度上限，℃，来自系统供电策略。0 = 未提供。</summary>
        public int TctlCelsius { get; set; }

        public DateTime CapturedUtc { get; set; }

        /// <summary>boot = 启动自动采集；refresh = 用户点「现读并覆盖」。</summary>
        public string Source { get; set; } = "boot";

        /// <summary>温度墙是否有可用值。</summary>
        public bool HasTctl => TctlCelsius > 0;

        /// <summary>PPT 是否有可用值。</summary>
        public bool HasPpt => PptMilliwatt > 0;

        /// <summary>TDC 是否有可用值。</summary>
        public bool HasTdc => TdcMilliamp > 0;

        public bool IsUsable => HasPpt || HasTdc || HasTctl;
    }

    public static class PowerBaselineStore
    {
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZenForge");

        private static readonly string FilePath = Path.Combine(DirectoryPath, "power-baseline.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static readonly object Sync = new();

        private static PowerBaseline? _cached;

        /// <summary>当前内存中的基线。首次访问时从磁盘加载。</summary>
        public static PowerBaseline? Current
        {
            get
            {
                lock (Sync)
                {
                    return _cached ??= LoadFromDisk();
                }
            }
        }

        private static PowerBaseline? LoadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                PowerBaseline? loaded = JsonSerializer.Deserialize<PowerBaseline>(File.ReadAllText(FilePath), Options);
                if (loaded == null || !loaded.IsUsable)
                {
                    LogService.Warn("功耗基线文件存在但内容不可用，按缺失处理");
                    return null;
                }
                return loaded;
            }
            catch (Exception ex)
            {
                LogService.Warn($"读取功耗基线失败，按缺失处理：{ex.Message}");
                return null;
            }
        }

        /// <summary>先写临时文件再原子替换。断电不会留下半截 JSON。</summary>
        public static bool Save(PowerBaseline baseline)
        {
            lock (Sync)
            {
                string temp = FilePath + ".tmp";
                try
                {
                    System.IO.Directory.CreateDirectory(DirectoryPath);
                    File.WriteAllText(temp, JsonSerializer.Serialize(baseline, Options));
                    File.Move(temp, FilePath, overwrite: true);
                    _cached = baseline;
                    return true;
                }
                catch (Exception ex)
                {
                    LogService.Warn($"写入功耗基线失败：{ex.Message}");
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    return false;
                }
            }
        }

        /// <summary>
        /// 基线是否属于当前这台机器。
        /// CPU 名或 SMU 版本任一不同就必须作废 —— 否则会拿旧机器的出厂值
        /// 去还原新机器，写进去的是一个既非出厂也非用户设定的陌生数字。
        /// </summary>
        public static bool Matches(PowerBaseline baseline, SmuSnapshot snapshot) =>
            string.Equals(baseline.CpuName, snapshot.CpuName, StringComparison.Ordinal) &&
            string.Equals(baseline.SmuVersion, snapshot.SmuVersion, StringComparison.Ordinal);

        /// <summary>丢弃内存与磁盘上的基线（换硬件时用）。</summary>
        public static void Clear()
        {
            lock (Sync)
            {
                _cached = null;
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            }
        }

        /// <summary>重新从磁盘读取，丢弃内存缓存。</summary>
        public static void InvalidateCache()
        {
            lock (Sync)
            {
                _cached = null;
            }
        }
    }
}
