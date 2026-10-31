using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// SMU 自检：把定位 CO / FMax 问题所需的信息一次性收集成文本报告。
    ///
    /// 设计约束：
    ///   1. 绝不改变用户的调校状态。写入测试只写「当前值」，写完立即回读比对；
    ///      若当前值本身读不到（int.MinValue），就跳过该核的写入测试。
    ///   2. 任何一步失败都不能中断整份报告 —— 失败本身就是最有价值的线索，
    ///      所以要记录异常再继续下一项。
    ///   3. 全程不弹窗、不碰 UI，由调用方在后台线程执行。
    /// </summary>
    public class SmuDiagnostics
    {
        private readonly ILogService _log;
        private readonly IAppPaths _paths;
        private readonly ICpuSetBinder _cpuSets;
        private readonly ProcessRefresher _refresher;
        private readonly IProcessWatcher _watcher;
        private readonly SmuReader _smu;

        public SmuDiagnostics(ILogService log, IAppPaths paths, ICpuSetBinder cpuSets, ProcessRefresher refresher, IProcessWatcher watcher, SmuReader smu)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
            ArgumentNullException.ThrowIfNull(cpuSets);
            _cpuSets = cpuSets;
            ArgumentNullException.ThrowIfNull(refresher);
            _refresher = refresher;
            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;
            ArgumentNullException.ThrowIfNull(smu);
            _smu = smu;
        }

        public string Run()
        {
            StringBuilder sb = new();
            void Line(string text = "") => sb.AppendLine(text);

            Line("================================================================");
            Line("  Zentwik SMU 自检报告");
            Line($"  生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Line("================================================================");
            Line();

            AppendEnvironment(sb);
            AppendTopology(sb);
            SmuSnapshot snapshot = AppendSnapshot(sb);
            AppendMaskDeduction(sb, snapshot);
            AppendWriteTest(sb, snapshot);
            AppendFMaxChannel(sb);
            AppendWatcherState(sb);

            Line("================================================================");
            Line("  报告结束");
            Line("================================================================");

            return Write(sb.ToString());
        }

        private void AppendEnvironment(StringBuilder sb)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 1. 环境 ----------
            Line("[1] 运行环境");
            try
            {
                Line($"    操作系统：{Environment.OSVersion}");
                Line($"    64 位进程：{Environment.Is64BitProcess}");
                Line($"    .NET 版本：{Environment.Version}");
                Line($"    处理器数：{Environment.ProcessorCount}");

                using WindowsIdentity id = WindowsIdentity.GetCurrent();
                bool admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                Line($"    当前用户：{id.Name}");
                Line($"    管理员权限：{(admin ? "是" : "否（写入必然失败）")}");
            }
            catch (Exception ex)
            {
                Line($"    !! 采集环境信息失败：{ex.Message}");
            }
            Line();
        }

        private void AppendTopology(StringBuilder sb)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 2. Zentwik 看到的核心拓扑 ----------
            Line("[2] Zentwik 侧拓扑（决定 CO 掩码怎么拆）");
            try
            {
                Line($"    逻辑处理器数（CpuSetBinder）：{_cpuSets.LogicalProcessorCount}");
                SmuReader reader = _smu;
                Line($"    每 CCD 核数（_smu.CoresPerCcd）：{reader.CoresPerCcd}");
                Line($"    每 CCD 核数（原始探测值，0=未探测）：{reader.CoresPerCcdRaw}");
                Line($"    SMU 可用：{reader.IsAvailable}");
                if (!string.IsNullOrEmpty(reader.LastError))
                    Line($"    SMU 初始化错误：{reader.LastError}");

                // 必须是 1。大于 1 说明有路径重复创建 Cpu，
                // 每多一个都会把 PawnIO 模块句柄换掉，先前的实例就读不到 SMU 了。
                Line($"    累计创建 Cpu 实例数：{SmuReader.CreatedCount}（正常应为 1）");
                if (SmuReader.CreatedCount > 1)
                    Line("    !! 实例数大于 1 —— 旧实例的 SMU 通道已失效，这就是读不到 CO / FMax 的原因");
            }
            catch (Exception ex)
            {
                Line($"    !! 采集拓扑失败：{ex.Message}");
            }
            Line();
        }

        private SmuSnapshot AppendSnapshot(StringBuilder sb)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 3. SMU 只读快照 ----------
            Line("[3] SMU 只读快照");
            SmuSnapshot snapshot = new();
            try
            {
                SmuReader reader = _smu;
                snapshot = reader.ReadSnapshot();

                if (!snapshot.Success)
                {
                    Line($"    !! 读取失败：{snapshot.Error}");
                }
                else
                {
                    Line($"    CPU：{snapshot.CpuName}");
                    Line($"    代号：{snapshot.CodeName}");
                    Line($"    家族：{snapshot.Family}");
                    Line($"    SMU 型号：{snapshot.SmuType}");
                    Line($"    SMU 版本：{snapshot.SmuVersion}");
                    Line($"    CCD 数：{snapshot.Ccds}");
                    Line($"    物理核：{snapshot.PhysicalCores}");
                    Line($"    每核线程：{snapshot.ThreadsPerCore}");
                    Line($"    每 CCD 核数：{snapshot.CoresPerCcd}");
                    Line($"    FMax 当前值：{snapshot.FMaxMHz} MHz");

                    // 这三项决定「是命令没通，还是命令不支持」
                    (string status, string? lastError) = _smu.CpuStateForDiagnostics();
                    Line($"    Cpu 状态：{status}");
                    Line($"    Cpu 最后错误：{(lastError ?? "(无)")}");

                    (bool testOk, string? testError) = _smu.TestMessageForDiagnostics();
                    Line($"    SMU 测试消息：{(testOk ? "通过" : "失败")}" +
                         (testError == null ? "" : $"（{testError}）"));

                    (uint getDldo, uint setDldo, uint setBoost, uint getBoost) =
                        _smu.SmuMsgIdsForDiagnostics();
                    Line("    SMU 消息号（0 = 该平台未初始化这条命令）：");
                    Line($"      GetDldoPsmMargin            = 0x{getDldo:X}");
                    Line($"      SetDldoPsmMargin            = 0x{setDldo:X}");
                    Line($"      SetBoostLimitFrequencyAll   = 0x{setBoost:X}");
                    Line($"      GetBoostLimitFrequency      = 0x{getBoost:X}");
                    Line();
                    Line("    每核 CO 当前值（int.MinValue = 读取失败 / 被熔断禁用）：");
                    for (int i = 0; i < snapshot.CoreMargins.Length; i++)
                    {
                        int v = snapshot.CoreMargins[i];
                        Line($"      核 {i,2}：{(v == int.MinValue ? "读取失败" : v.ToString())}");
                    }
                }
            }
            catch (Exception ex)
            {
                Line($"    !! 读取快照异常：{ex}");
            }
            Line();

            return snapshot;
        }

        private void AppendMaskDeduction(StringBuilder sb, SmuSnapshot snapshot)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 4. CO 掩码推演 ----------
            Line("[4] CO 掩码推演（不做任何写入）");
            try
            {
                SmuReader reader = _smu;
                int cores = snapshot.Success ? (int)snapshot.PhysicalCores : 0;
                if (cores <= 0)
                {
                    Line("    跳过：核数未知");
                }
                else
                {
                    Line("    逐核把「线性序号」拆成 (core, ccd, ccx)，并由库计算掩码。");
                    Line("    若这些值与 mvolt / SMUDebugTool 显示的不一致，就是拆分口径错了。");
                    Line();
                    for (int i = 0; i < cores; i++)
                    {
                        (uint core, uint ccd, uint ccx) = reader.SplitCoreForDiagnostics(i);
                        Line($"      核 {i,2} → core={core} ccd={ccd} ccx={ccx}");
                    }
                }
            }
            catch (Exception ex)
            {
                Line($"    !! 推演失败：{ex.Message}");
            }
            Line();
        }

        private void AppendWriteTest(StringBuilder sb, SmuSnapshot snapshot)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 5. 写入回滚测试 ----------
            Line("[5] 写入测试（只写当前值，等价于不改动）");
            try
            {
                SmuReader reader = _smu;
                if (!reader.IsAvailable)
                {
                    Line("    跳过：SMU 不可用");
                }
                else if (!snapshot.Success)
                {
                    Line("    读不到当前值，无法「写回原值」；改为写入 CO=0（无偏移＝出厂态，安全）");
                    try
                    {
                        (uint c0, uint ccd0, uint ccx0) = reader.SplitCoreForDiagnostics(0);
                        bool ok = reader.TrySetCoForDiagnostics(c0, ccd0, ccx0, 0);
                        Line($"      核 0 写入 0 → 接受={ok}");
                        if (!ok)
                            Line("      !! 连 CO=0 都被拒：说明写入通道本身不通，与数值无关。");
                    }
                    catch (Exception ex)
                    {
                        Line($"      核 0 写入 0 → 异常：{ex.Message}");
                    }
                }
                else
                {
                    // 逐核写回它自己的当前值
                    int tested = 0, accepted = 0;
                    for (int i = 0; i < snapshot.CoreMargins.Length; i++)
                    {
                        int current = snapshot.CoreMargins[i];
                        if (current == int.MinValue)
                            continue;

                        tested++;
                        try
                        {
                            (uint core, uint ccd, uint ccx) = reader.SplitCoreForDiagnostics(i);
                            uint mask = reader.MakeCoreMaskForDiagnostics(core, ccd, ccx);
                            bool ok = reader.TrySetCoForDiagnostics(core, ccd, ccx, current);
                            if (ok) accepted++;

                            Line($"      核 {i,2} mask=0x{mask:X8} 写回 {current} → 接受={ok}");
                        }
                        catch (Exception ex)
                        {
                            Line($"      核 {i,2} 写回 {current} → 异常：{ex.Message}");
                        }
                    }
                    Line();
                    Line($"    小结：测试 {tested} 核，接受 {accepted} 核");
                    if (tested > 0 && accepted == 0)
                    {
                        Line("    !! 全部被拒 —— 说明 SMU 不接受我们计算的核心掩码，");
                        Line("       或该平台不支持 CO 写入（部分移动平台确实锁了这条消息）。");
                    }
                }
            }
            catch (Exception ex)
            {
                Line($"    !! 写入测试失败：{ex}");
            }
            Line();
        }

        private void AppendFMaxChannel(StringBuilder sb)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 6. FMax 通道 ----------
            Line("[6] FMax 读取通道");
            try
            {
                SmuReader reader = _smu;
                if (!reader.IsAvailable)
                {
                    Line("    跳过：SMU 不可用");
                }
                else
                {
                    uint baseline = reader.GetFMaxForDiagnostics();
                    Line($"    当前读取值：{baseline} MHz");
                    if (baseline == 0)
                    {
                        Line("    !! 读到 0 —— 这台机器的 FMax 读取通道很可能不可用。");
                        Line("       这种情况下「写入后回读 0」不能判定为写入失败，");
                        Line("       而是根本无法验证。Zentwik 已按此区分处理。");
                    }
                }
            }
            catch (Exception ex)
            {
                Line($"    !! FMax 测试失败：{ex}");
            }
            Line();
        }

        private void AppendWatcherState(StringBuilder sb)
        {
            void Line(string text = "") => sb.AppendLine(text);

            // ---------- 7. 绑定监听模式 ----------
            Line("[7] 进程绑核监听");
            try
            {
                Line($"    运行中：{_watcher.IsRunning}");
                Line($"    WMI 事件模式：{_watcher.UsingEvents}");
                Line($"    进程快照采样中（有人看列表）：{_refresher.IsWatched}");
            }
            catch (Exception ex)
            {
                Line($"    !! 采集失败：{ex.Message}");
            }
            Line();
        }

private string Write(string content)
        {
            string dir = _paths.Dir("Diagnostics");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"smu-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }
    }
}
