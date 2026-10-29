using System;
using System.Threading;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// 启动时的出厂基线采集。
    ///
    /// 只做一件事：进程起来后，SMU 一就绪就把四项墙的出厂值读一遍并落盘。
    /// 之后「恢复默认」永远读这份文件，而不是现读 —— 理由见 PowerBaselineStore 的类注释。
    ///
    /// 两条宿主路径（GUI 正常启动、-silent 后台自启）都会调用，
    /// 用 _done 保证同一进程内只跑一次，避免重复读 SMU 无谓拉长启动时间。
    /// </summary>
    public static class PowerBaselineBootstrap
    {
        private static int _done;

        /// <summary>
        /// 后台线程入口：自己等 PawnIO、自己探测。不阻塞调用方。
        /// </summary>
        public static void RunInBackground(string reason)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    SmuReader? reader = SmuReader.WaitForPawnIO(60);
                    if (reader == null)
                    {
                        LogService.Warn($"功耗基线（{reason}）：PawnIO 60 秒内未就绪，跳过采集");
                        return;
                    }

                    SmuSnapshot snapshot = reader.ReadSnapshot();
                    if (!snapshot.Success)
                    {
                        LogService.Warn($"功耗基线（{reason}）：SMU 探测失败（{snapshot.Error}），跳过采集");
                        return;
                    }

                    Ensure(reader, snapshot, reason);
                }
                catch (Exception ex)
                {
                    LogService.Error($"功耗基线（{reason}）采集异常：{ex}");
                }
            });
        }

        /// <summary>
        /// 已有可用 reader 与快照时直接调用（静默模式写入流程里顺手执行）。
        /// </summary>
        public static void Ensure(SmuReader reader, SmuSnapshot snapshot, string reason)
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
                return;

            try
            {
                PowerBaseline? saved = PowerBaselineStore.Current;

                // 换过 CPU 或刷过 BIOS：旧基线一律作废，不参与比对也不参与恢复。
                if (saved != null && !PowerBaselineStore.Matches(saved, snapshot))
                {
                    LogService.Warn(
                        $"功耗基线不匹配，已作废：记录为 {saved.CpuName} / SMU {saved.SmuVersion}，" +
                        $"当前为 {snapshot.CpuName} / SMU {snapshot.SmuVersion}");
                    PowerBaselineStore.Clear();
                    saved = null;
                }

                PowerBaseline? fresh = reader.CaptureBaseline("boot");
                if (fresh == null || !fresh.IsUsable)
                {
                    LogService.Warn($"功耗基线（{reason}）：本次未采集到可用值，恢复默认将不可用");
                    return;
                }

                if (saved == null)
                {
                    if (PowerBaselineStore.Save(fresh))
                    {
                        LogService.Info(
                            $"功耗基线已建立：PPT {fresh.PptMilliwatt} mW / " +
                            $"TDC {fresh.TdcMilliamp} mA / Tctl {fresh.TctlCelsius} ℃（EDC 无通道）");
                    }
                    return;
                }

                // 存档已存在：只比对、只记日志，不覆盖。
                // 存档是出厂值；现读可能是被改过的生效值，拿现读覆盖等于把污染固化成基线。
                LogIfDiffers("PPT", saved.PptMilliwatt, fresh.PptMilliwatt, "mW");
                LogIfDiffers("TDC", saved.TdcMilliamp, fresh.TdcMilliamp, "mA");
                LogIfDiffers("Tctl", saved.TctlCelsius, fresh.TctlCelsius, "℃");
            }
            catch (Exception ex)
            {
                LogService.Error($"功耗基线校验异常：{ex}");
            }
        }

        private static void LogIfDiffers(string name, int saved, int fresh, string unit)
        {
            if (saved == fresh || fresh == 0)
                return;

            LogService.Warn(
                $"功耗基线偏差：{name} 存档 {saved} {unit} / 现读 {fresh} {unit}。" +
                $"存档值保持不变；若确认现读才是对的，在调校页点「现读并覆盖」");
        }
    }
}
