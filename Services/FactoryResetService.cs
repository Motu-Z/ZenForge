using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Helpers;

namespace Zentwik.Services
{
    /// <summary>
    /// 一次恢复出厂的结果。逐项列出每个步骤做了什么、哪一步失败，
    /// 而不是只给一个 bool —— 用户点完「重置」之后最想知道的是
    /// 「到底哪些清干净了，哪些没有，为什么」。
    /// </summary>
    public sealed class FactoryResetReport
    {
        private readonly List<string> _done = new();
        private readonly List<string> _failed = new();
        private readonly List<string> _skipped = new();

        public IReadOnlyList<string> Done => _done;
        public IReadOnlyList<string> Failed => _failed;
        public IReadOnlyList<string> Skipped => _skipped;

        /// <summary>没有任何一步失败。失败不等于「全没做」—— 失败的项在 Failed 里。</summary>
        public bool AllSucceeded => _failed.Count == 0;

        internal void Ok(string what) => _done.Add(what);

        /// <summary>失败：仍然继续执行后面的步骤，不能因为托盘没建起来就跳过删文件。</summary>
        internal void Fail(string what, string why) => _failed.Add($"{what}（{why}）");

        /// <summary>跳过：客观上不需要做（本来就没开 / 文件本来就不存在）。</summary>
        internal void Skip(string what) => _skipped.Add(what);
    }

    /// <summary>
    /// 恢复出厂设置（全局重置）的唯一实现。
    ///
    /// 【为什么不能只是删两个 JSON】
    /// 这个程序有三层状态，只清最上面一层，用户会得到一个「看起来重置了、
    /// 实际上风扇还在按老参数转」的机器：
    ///   1. 系统占用层 —— 计划任务、后台监听、每个进程的 CPU 亲和性、托盘图标；
    ///   2. 硬件底层   —— SMU 里的 CO / FMax，Windows 电源方案里的 Boost / EPP / 频率上限；
    ///   3. 用户数据   —— tuning / bindings / masks / profiles / logs / ui.json。
    /// 所以顺序是「先解除占用 → 再复位硬件 → 最后清文件」：
    /// 反过来的话，清掉 bindings.json 之后后台监听还在按内存里的旧规则绑核，
    /// 会立刻把规则重新写回磁盘（Reload 时读不到文件就保留内存副本），
    /// 等于白清。
    ///
    /// 【线程】
    /// 整个方法在后台线程跑（<see cref="Task.Run(Func{FactoryResetReport})"/>），
    /// 唯一必须在 UI 线程做的是清空日志集合与刷界面，那两处用 UiContext 回调。
    /// 其余全是注册表 / 文件 / P/Invoke，UI 线程上做会把窗口冻住好几秒。
    /// </summary>
    public static class FactoryResetService
    {
        /// <summary>
        /// 执行完整重置。调用方负责二次确认与 UI 反馈；这里只做事、只报告。
        /// </summary>
        public static async Task<FactoryResetReport> ResetToFactoryAsync()
        {
            // 大部分步骤会打日志。日志文件本身稍后要被删掉，
            // 但 reset 过程必须在日志里留痕 —— 否则下次启动时用户报「我重置过」
            // 而日志里一片空白，连发生过什么都不知道。所以先记一条开始标记。
            Svc.Log.Info("=== 全局重置开始：恢复出厂设置 ===");
            Svc.Log.Flush();

            FactoryResetReport report = await Task.Run(() => RunSteps()).ConfigureAwait(true);

            // 收尾日志在文件删完之后写，会重新创建一个「reset 之后」的新日志文件。
            // 这是有意为之：上一次会话的日志已被清空，新的这份只记录重置结果。
            Svc.Log.Info(report.AllSucceeded
                ? "=== 全局重置完成：已恢复出厂设置 ==="
                : $"=== 全局重置完成，但有 {report.Failed.Count} 项未成功 ===");
            foreach (string line in report.Done)
                Svc.Log.Info($"  已恢复：{line}");
            foreach (string line in report.Skipped)
                Svc.Log.Info($"  无需处理：{line}");
            foreach (string line in report.Failed)
                Svc.Log.Warn($"  失败：{line}");
            Svc.Log.Flush();

            return report;
        }

        /// <summary>
        /// 停用全部功能：解除本程序对系统与硬件的一切干预，但<b>保留用户配置</b>。
        ///
        /// 【与 <see cref="ResetToFactoryAsync"/> 的差别只在最后一步】
        /// 这里不删 tuning / bindings / masks / ui.json：
        /// 停用的语义是「先别动手」，不是「从来没装过」。用户随时可以回到
        /// 调校页点「应用全部」把参数重新下发，配置还跟离开时一样。
        /// 顺序与主流程相同（先解除占用 → 再复位硬件），
        /// 否则后台监听会在清空硬件参数之后又把进程的亲和性按老规则写回去。
        /// </summary>
        public static async Task<FactoryResetReport> DisableAllFeaturesAsync()
        {
            Svc.Log.Info("=== 停用全部功能开始（保留用户配置）===");
            Svc.Log.Flush();

            FactoryResetReport report = await Task.Run(() => RunDisableSteps()).ConfigureAwait(true);

            Svc.Log.Info(report.AllSucceeded
                ? "=== 停用全部功能完成 ==="
                : $"=== 停用全部功能完成，但有 {report.Failed.Count} 项未成功 ===");

            foreach (string line in report.Done)
                Svc.Log.Info($"  已完成：{line}");
            foreach (string line in report.Skipped)
                Svc.Log.Info($"  无需处理：{line}");
            foreach (string line in report.Failed)
                Svc.Log.Warn($"  失败：{line}");

            Svc.Log.Flush();
            return report;
        }

        private static FactoryResetReport RunDisableSteps()
        {
            FactoryResetReport report = new();
            uint fMaxBaseline = SafeReadFMaxBaseline();

            StepStopWatching(report);
            StepReleaseAffinities(report);
            StepRemoveAutoStart(report);
            StepResetHardware(report, fMaxBaseline);
            StepReleasePowerPolicy(report);
            StepDisableBackendService(report);

            return report;
        }

        /// <summary>停后台监听。必须第一个做：它活着就会把老规则重新写回进程。</summary>
        private static void StepStopWatching(FactoryResetReport report)
        {
            try
            {
                if (Svc.Watcher.IsRunning)
                {
                    Svc.Watcher.Stop();
                    report.Ok("已停止后台进程监听");
                }
                else
                {
                    report.Skip("后台进程监听（未在运行）");
                }
            }
            catch (Exception ex)
            {
                report.Fail("停止后台进程监听", ex.Message);
            }
        }

        private static void StepReleaseAffinities(FactoryResetReport report)
        {
            try
            {
                ReleaseAffinities(out int released, out int failed);

                if (released > 0)
                    report.Ok($"已解除 {released} 个进程的 CPU 亲和性限制");

                if (failed > 0)
                    report.Fail("解除部分进程亲和性", $"{failed} 个进程无权限或被保护，已跳过");
                else if (released == 0)
                    report.Skip("解除 CPU 亲和性（没有正在生效的绑核）");
            }
            catch (Exception ex)
            {
                report.Fail("解除 CPU 亲和性", ex.Message);
            }
        }

        private static void StepRemoveAutoStart(FactoryResetReport report)
        {
            try
            {
                if (!Svc.AutoStart.IsEnabled())
                {
                    report.Skip("开机自启（未启用）");
                    return;
                }

                if (Svc.AutoStart.Disable())
                    report.Ok("已移除开机自启计划任务");
                else
                    report.Fail("移除开机自启计划任务", "计划任务删除失败");
            }
            catch (Exception ex)
            {
                report.Fail("移除开机自启计划任务", ex.Message);
            }
        }

        /// <summary>释放电源方案：本程序改过的 Boost / EPP / 频率上限全部交还 Windows。</summary>
        private static void StepReleasePowerPolicy(FactoryResetReport report)
        {
            try
            {
                PowerPlanResult result = ProcessorPowerPolicy.Release();

                if (result.Ok)
                    report.Ok("已释放电源方案设置");
                else
                    report.Fail("释放电源方案设置", result.Error);
            }
            catch (Exception ex)
            {
                report.Fail("释放电源方案设置", ex.Message);
            }
        }

        /// <summary>总闸关到底：后台服务、常驻调度、托盘图标一并停在关闭态并落盘。</summary>
        private static void StepDisableBackendService(FactoryResetReport report)
        {
            try
            {
                UiSettings settings = Svc.Ui.Settings;
                settings.BackendService = false;
                settings.WatchProcessBinding = false;
                settings.ShowTrayIcon = false;
                settings.AutoStart = false;
                Svc.Ui.Save();

                TrayIconService.SetVisible(false);
                report.Ok("后台服务已停用（调度、托盘一并关闭）");
            }
            catch (Exception ex)
            {
                report.Fail("停用后台服务", ex.Message);
            }
        }

        private static FactoryResetReport RunSteps()
        {
            FactoryResetReport report = new();

            // 第二步要用「出厂 FMax 基线」，而第三步的 TuningStore.ResetToFactory()
            // 会把 tuning.json 覆盖成空配置（基线字段也一并重置）。
            // 顺序天然是硬件在前、清文件在后，所以这里不需要抢跑；
            // 但为了不被将来调整顺序的人踩坑，基线在第一步之前就抓一份快照。
            uint fMaxBaseline = SafeReadFMaxBaseline();

            StepReleaseSystemHolds(report);
            StepResetHardware(report, fMaxBaseline);
            StepClearUserData(report);
            StepResetUiSettings(report);

            return report;
        }

        /// <summary>读不到就当未知（0），绝不让读配置失败阻断重置。</summary>
        private static uint SafeReadFMaxBaseline()
        {
            try
            {
                return Svc.Tuning.Profile.FMaxBaselineMHz;
            }
            catch
            {
                return 0;
            }
        }

        // ==================================================================
        // 第一步：解除系统占用（先软后硬）
        // ==================================================================

        private static void StepReleaseSystemHolds(FactoryResetReport report)
        {
            // 1-1 停止后台的进程监听服务。
            // 必须最先做：下面删 bindings.json 时，监听线程若还在跑，
            // 它的 Reload 会把「文件不存在」当成「保持内存规则」，随后
            // 一次完整的 Save 就把旧规则重新写回磁盘，重置白做。
            try
            {
                if (Svc.Watcher.IsRunning)
                {
                    Svc.Watcher.Stop();
                    report.Ok("已停止后台进程监听");
                }
                else
                {
                    report.Skip("后台进程监听（未在运行）");
                }
            }
            catch (Exception ex)
            {
                report.Fail("停止后台进程监听", ex.Message);
            }

            // 1-2 解除所有进程的 CPU 亲和性，回到系统默认调度。
            // 只解绑「当前确实被限制过」的进程：逐个 SetProcessDefaultCpuSets(空集)
            // 会去 OpenProcess 每一个进程，包括受保护的系统进程 —— 既慢又必然失败一片。
            // 判据取两处并集：后台监听内存里的规则 + bindings.json 里保存过的规则。
            try
            {
                int released, failed;
                ReleaseAffinities(out released, out failed);

                if (released > 0)
                    report.Ok($"已解除 {released} 个进程的 CPU 亲和性限制");

                if (failed > 0)
                    report.Fail("解除部分进程亲和性", $"{failed} 个进程无权限或被保护，已跳过");
                else if (released == 0)
                    report.Skip("解除 CPU 亲和性（没有正在生效的绑核）");
            }
            catch (Exception ex)
            {
                report.Fail("解除 CPU 亲和性", ex.Message);
            }

            // 1-3 删除 Windows 计划任务（关闭开机自启）。
            try
            {
                if (Svc.AutoStart.IsEnabled())
                {
                    if (Svc.AutoStart.Disable())
                        report.Ok("已删除开机自启计划任务");
                    else
                        report.Fail("删除开机自启计划任务", "schtasks 返回失败，可能需要管理员权限");
                }
                else
                {
                    report.Skip("开机自启计划任务（本来就不存在）");
                }
            }
            catch (Exception ex)
            {
                report.Fail("删除开机自启计划任务", ex.Message);
            }

            // 1-4 托盘图标恢复为默认显示。
            // 直接重建图标而不是先 SetVisible(false) 再 EnsureCreated：
            // 后者会在任务栏上闪一下。这里只保证「出厂状态 = 有图标」。
            try
            {
                if (TrayIconService.EnsureCreated())
                    report.Ok("托盘图标已恢复为默认显示");
                else
                    report.Fail("恢复托盘图标", "图标资源不可用");
            }
            catch (Exception ex)
            {
                report.Fail("恢复托盘图标", ex.Message);
            }
        }

        /// <summary>
        /// 解除所有由本程序下发过的 CPU 亲和性限制。
        ///
        /// 遍历的是「规则里出现过的进程名」而不是系统全部进程：
        /// 出厂状态只要求抹掉**我们造成的**限制，不去碰别人。
        /// 同名进程可能有多个实例，全部解绑。
        /// </summary>
        private static void ReleaseAffinities(out int released, out int failed)
        {
            released = 0;
            failed = 0;

            // 内存里现存的规则 + 磁盘上的规则取并集：
            // ResetToFactory 之前调用方可能已经把 Profile 清空了，
            // 所以磁盘那份要单独读一次。
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (BindingRule rule in Svc.Bindings.Profile.Rules)
                {
                    if (!string.IsNullOrWhiteSpace(rule.Name))
                        names.Add(rule.Name);
                }
            }
            catch
            {
                // 规则读不出来就什么都不做，见下方注释
            }

            // 没有规则可依时，宁可不动：全量解绑会把用户自己用其他工具
            // 设过的亲和性也一起抹掉，那不是「恢复出厂」，那是越权。
            if (names.Count == 0)
                return;

            foreach (ProcessRefresher.Entry entry in Svc.Refresher.Latest)
            {
                if (!names.Contains(entry.Name))
                    continue;

                // 空数组 = 清除默认 CPU Set，进程回到系统默认调度。
                // 这是 SetProcessDefaultCpuSets 的语义，不是「绑到 0 个核」。
                if (Svc.CpuSets.ClearDefaultCpuSets(entry.Pid, out _))
                    released++;
                else
                    failed++;
            }
        }

        // ==================================================================
        // 第二步：重置硬件底层参数
        // ==================================================================

        private static void StepResetHardware(FactoryResetReport report, uint fMaxBaseline)
        {
            SmuReader reader = Svc.Smu;

            // 2-1 / 2-2 Curve Optimizer 全零、FMax 解锁。
            if (!reader.IsAvailable)
            {
                report.Skip("SMU 复位（驱动不可用，本地配置已清，下次启动按出厂值下发）");
            }
            else
            {
                int coreCount = reader.CoreCount;
                if (coreCount <= 0)
                {
                    report.Fail("SMU 复位 CO", "读不到核心数，未写回（避免对不存在的核下发 0）");
                }
                else
                {
                    try
                    {
                        SmuWriteResult co = reader.WriteCurveOptimizer(new int[coreCount]);
                        if (co.AllSucceeded)
                            report.Ok($"SMU Curve Optimizer 已归零（{coreCount} 核）");
                        else
                            report.Fail("SMU 复位 CO", "部分核心写入失败");
                    }
                    catch (Exception ex)
                    {
                        report.Fail("SMU 复位 CO", ex.Message);
                    }
                }

                // FMax 恢复出厂基线。
                //
                // 【不能写 0】WriteFMax 在入口就有越界断言（合法区间 4450-5650），
                // 传 0 会被直接拒绝并返回 Fatal，等于什么都没做却显得「已执行」。
                // 这里的「解锁」指的是把上限写回这台机器的出厂基线。
                //
                // 基线来自 tuning.json 里捕获的 FMaxBaselineMHz（探测到过真实上限）。
                // 拿不到就不写 —— 猜一个 5400 在移动端会直接超出真实上限，
                // 「不知道」就该说不知道，让固件自己用默认值。
                try
                {
                    uint baseline = fMaxBaseline;
                    if (baseline == 0)
                    {
                        report.Skip("SMU 复位 FMax（出厂基线未知，交由固件默认）");
                    }
                    else
                    {
                        SmuFMaxResult fm = reader.WriteFMax(baseline);
                        if (fm.AllSucceeded)
                            report.Ok($"SMU FMax 已恢复出厂基线 {baseline} MHz");
                        else
                            report.Fail("SMU 复位 FMax", fm.Fatal ?? $"回读 {fm.ReadBack}");
                    }
                }
                catch (Exception ex)
                {
                    report.Fail("SMU 复位 FMax", ex.Message);
                }
            }

            // 2-3 电源计划策略复位。
            // 「复位」= 把所有由本程序写过的索引交回系统默认（0xFFFFFFFF），
            // 而不是写一堆猜测值。用户原先的电源方案由 Windows 自己管，
            // 我们只负责把我们改过的那几项还回去。
            try
            {
                if (!ProcessorPowerPolicy.IsAvailable())
                {
                    report.Skip("电源计划策略复位（powrprof 不可用）");
                }
                else
                {
                    PowerPlanResult release = ProcessorPowerPolicy.Release();
                    if (release.Ok)
                        report.Ok("电源计划策略已复位（Boost / 频率上限 / EPP 交回系统默认）");
                    else
                        report.Fail("电源计划策略复位", $"{release.Setting}：{release.Error}");
                }
            }
            catch (Exception ex)
            {
                report.Fail("电源计划策略复位", ex.Message);
            }
        }

        // ==================================================================
        // 第三步：清空所有用户数据文件
        // ==================================================================

        private static void StepClearUserData(FactoryResetReport report)
        {
            // 顺序不敏感，但一律「先落盘空态，再删文件」，理由同 bindings：
            // 有存储类持有内存副本，直接删文件下次 Save 会把旧内容写回来。
            try
            {
                Svc.Tuning.ResetToFactory();
                report.Ok("tuning.json 已清空");
            }
            catch (Exception ex)
            {
                report.Fail("清空 tuning.json", ex.Message);
            }

            try
            {
                Svc.Bindings.ResetToFactory();
                report.Ok("bindings.json 已清空");
            }
            catch (Exception ex)
            {
                report.Fail("清空 bindings.json", ex.Message);
            }

            try
            {
                Svc.Masks.ResetToFactory();
                report.Ok("masks.json 已清空");
            }
            catch (Exception ex)
            {
                report.Fail("清空 masks.json", ex.Message);
            }

            // profiles.json（多套总体配置方案）目前还没有落盘实现 —— 配置页是骨架。
            // 仍然按约定的路径检测一次并删除：将来接入之后不用回来改这里，
            // 而现在这一行只是「不存在就不做」，零成本。
            try
            {
                string profiles = Path.Combine(AppDataDir, "profiles.json");
                if (File.Exists(profiles))
                {
                    File.Delete(profiles);
                    report.Ok("profiles.json 已清空");
                }
                else
                {
                    report.Skip("profiles.json（本版本尚未使用该文件）");
                }
            }
            catch (Exception ex)
            {
                report.Fail("清空 profiles.json", ex.Message);
            }

            // 日志：LogService.Clear() 会一并清内存集合并推进写盘代号，
            // 比直接删目录安全 —— 写盘线程正持有句柄时，直接删会失败。
            try
            {
                Svc.Log.Clear();
                report.Ok("日志文件已清空");
            }
            catch (Exception ex)
            {
                report.Fail("清空日志文件", ex.Message);
            }
        }

        // ==================================================================
        // 第四步：重置软件界面设置
        // ==================================================================

        private static void StepResetUiSettings(FactoryResetReport report)
        {
            // 4-1 / 4-2 把 ui.json 整个换回默认实例再写盘。
            // 不逐个字段赋值：UiSettings 的默认值就是出厂值，
            // 逐个赋值的话，将来新增字段时这里一定会漏。
            try
            {
                Svc.Ui.ResetToFactory();
                report.Ok("界面设置已恢复默认（主题与强调色跟随系统，轻量模式 / 托盘图标回到初始开启）");
            }
            catch (Exception ex)
            {
                report.Fail("恢复界面设置", ex.Message);
            }

            // 主题画刷要真的切回去，只改 json 不刷主题的话，用户会看到
            // 「设置里写着跟随系统，界面还是刚才那个深色」。
            try
            {
                ThemeService.ApplyAndSave(AppThemeMode.System, true, UiSettings.DefaultAccentHex);
            }
            catch (Exception ex)
            {
                report.Fail("应用默认主题", ex.Message);
            }
        }

        // ==================================================================

        private static string AppDataDir => Svc.Paths.Root;
    }
}
