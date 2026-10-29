using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 调校页（S3 完整版）。
    /// 三个入口彼此独立：
    ///   · CO 区「应用」        —— 只写 CO，绝不碰 FMax
    ///   · FMax 区「应用」      —— 只写 FMax，绝不碰 CO
    ///   · 顶部「全部应用并保存」 —— CO + FMax 一起写入
    /// CO 允许 [-50, +50]（负值降压 / 正值加压），仅校验整数合法性，不做安全拦截。
    /// </summary>
    public partial class TuningPage : Page
    {
        // 与 SmuReader 的输入合法性边界保持一致
        private const int CoMin = SmuReader.CoMin;
        private const int CoMax = SmuReader.CoMax;
        private const uint FMaxMin = SmuReader.FMaxMin;
        private const uint FMaxMax = SmuReader.FMaxMax;

        private bool _busy; // 防止并发点击重复下发

        public TuningPage()
        {
            InitializeComponent();

            // 核心输入框不在构造时生成：不同 CPU 的核心数 / CCD 数不同，
            // 必须等 SMU 只读探测拿到快照后，由 ApplySnapshot 按真实核心数
            // 调用 BuildCoreGrid 延迟构建（见 Z-2）。
            Loaded += async (_, _) => await ProbeSmuAsync();
        }

        /// <summary>
        /// 按真实核心数构建 CO 输入框网格（Z-2 修复：原版写死 2 CCD × 8 核 = 16 个框，
        /// 非 16 核 CPU 会静默丢核或留空框）。行数由 ceil(totalCores / coresPerCcd) 决定，
        /// 不再假设固定 2 行。每次重建前清空旧网格与列表，换 CPU / 驱动升级后能自愈。
        /// </summary>
        private void BuildCoreGrid(int totalCores, int coresPerCcd)
        {
            CoreGrid.Children.Clear();
            CoreBoxes.Clear();

            // 显式取 Style 而非依赖隐式查找：隐式样式在 Page 层可能被局部覆盖而回落系统默认白底；
            // 只赋 Style，不设局部 Background/Foreground/BorderBrush（局部值会压掉「聚焦青边」触发器）。
            Style? coreBoxStyle = TryFindResource("TuningBoxStyle") as Style;
            int rowCount = (int)Math.Ceiling(totalCores / (double)coresPerCcd);

            for (int r = 0; r < rowCount; r++)
            {
                Grid line = new() { Margin = new Thickness(0, 0, 0, 8) };

                // 第 0 列：CCD 标签（固定宽 44，保证各行输入框左边缘对齐）
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

                TextBlock tag = new()
                {
                    Text = $"CCD{r}",
                    Style = (Style)FindResource("CcdTagStyle")
                };
                Grid.SetColumn(tag, 0);
                line.Children.Add(tag);

                for (int c = 0; c < coresPerCcd; c++)
                {
                    int core = r * coresPerCcd + c;
                    if (core >= totalCores) break;   // 末行可能不满一行

                    // 等分剩余宽度
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    TextBox box = new()
                    {
                        Tag = core,
                        IsEnabled = false,
                        Style = coreBoxStyle,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, c == coresPerCcd - 1 ? 0 : 8, 0),
                        ToolTip = $"核心 {core}（CCD{r}）"
                    };

                    Grid.SetColumn(box, c + 1);
                    line.Children.Add(box);
                    CoreBoxes.Add(box);
                }

                CoreGrid.Children.Add(line);
            }
        }

        /// <summary>
        /// 全部 16 个 CO 输入框（按核心号索引）。
        /// CoreGrid.Children 里装的是"行"（Grid），不是输入框，
        /// 所有回填与收集逻辑统一走这张表。
        /// </summary>
        private readonly List<TextBox> CoreBoxes = new();

        /// <summary>
        /// 统一的页头状态写入。
        ///
        /// 重构说明：原来页头是「26px 大字(SmuStatusValue) + 小字(SmuBadge)」两套
        /// 互相独立的文本，17 处调用要分别设两个值，很容易出现「大字说就绪、
        /// 小字说失败」这种自相矛盾。现在改成单一状态徽章 + 明确的状态枚举，
        /// 只要传一个状态，文案和配色一起变。
        /// </summary>
        private enum SmuUiState
        {
            Probing,   // 探测中
            Ready,     // 就绪
            Writing,   // 写入中
            Applied,   // 已应用
            Failed     // 失败
        }

        /// <summary>
        /// 旧的状态徽章（SmuBadge）已移除。保留此方法仅为兼容既有调用点，不再渲染任何 UI。
        /// 失败的探测仍由 <see cref="ProbeSmuAsync"/> 里的 DarkDialog.ShowWarning 提示。
        /// </summary>
        private void SetSmuState(SmuUiState state, string? detail = null)
        {
            // 就绪 / 写入 / 应用 / 失败状态不再有可视徽章；保留日志以便排查。
            LogService.Info($"SMU 状态变更：{state}{(string.IsNullOrWhiteSpace(detail) ? "" : $" · {detail}")}");
        }

        /// <summary>在后台线程执行只读探测，完成后回 UI 线程刷新界面。</summary>
        private async System.Threading.Tasks.Task ProbeSmuAsync()
        {
            SetSmuState(SmuUiState.Probing, "ZenStates-Core 只读探测");

            SmuSnapshot? snapshot = null;
            string? fatalError = null;

            try
            {
                snapshot = await System.Threading.Tasks.Task.Run(() =>
                {
                    SmuReader reader = SmuReader.Shared;
                    if (!reader.IsAvailable)
                        throw new InvalidOperationException(reader.LastError ?? "ZenStates-Core 初始化失败");
                    return reader.ReadSnapshot();
                });
            }
            catch (Exception ex)
            {
                fatalError = ex.Message;
            }

            if (snapshot == null || !snapshot.Success)
            {
                LogService.Error($"SMU 只读探测失败: {snapshot?.Error ?? fatalError}");
                SetSmuState(SmuUiState.Failed, snapshot?.Error ?? fatalError);
                DarkDialog.ShowWarning(
                    "读取失败，请确认已安装 PawnIO 驱动并以管理员身份运行。\n\n详细信息已写入日志（设置 → 日志）。",
                    "SMU 探测失败");
                return;
            }

            ApplySnapshot(snapshot);
        }

        private void ApplySnapshot(SmuSnapshot snapshot)
        {
            // 页头只保留 CPU 型号一行（副标题的规格说明已移除，界面极简化）
            if (!string.IsNullOrWhiteSpace(snapshot.CpuName))
                CpuNameText.Text = snapshot.CpuName;

            SetSmuState(SmuUiState.Ready, $"SMU {snapshot.SmuVersion} · {snapshot.SmuType}");

            int coresPerCcd = (int)Math.Max(1, snapshot.CoresPerCcd);
            int totalCores = snapshot.CoreMargins.Length;
            // 核心数变化时才重建网格（换 CPU / 驱动升级自愈）；否则保留用户已填的值。
            if (totalCores > 0 && CoreBoxes.Count != totalCores)
                BuildCoreGrid(totalCores, coresPerCcd);
            int filled = 0;
            for (int i = 0; i < CoreBoxes.Count && i < snapshot.CoreMargins.Length; i++)
            {
                TextBox box = CoreBoxes[i];

                int margin = snapshot.CoreMargins[i];
                if (margin == int.MinValue)
                {
                    box.Text = "--";
                    box.IsEnabled = false;
                    box.ToolTip = $"核心 {i}（CCD{i / coresPerCcd}）· 读取失败或已被熔断禁用";
                }
                else
                {
                    box.Text = margin.ToString();
                    box.IsEnabled = true;
                    box.ToolTip = $"核心 {i}（CCD{i / coresPerCcd}）· 当前 CO 值（负值 = 降压）";
                    filled++;
                }
            }
            LogService.Info($"界面已回填 {filled} 个核心的 CO 值");

            if (snapshot.FMaxMHz > 0)
            {
                FMaxBox.Text = snapshot.FMaxMHz.ToString();
                LogService.Info($"FMax 输入框已回填：{snapshot.FMaxMHz} MHz");
            }

            // 记录出厂基线：首次从 SMU 读到就存盘（回落 5400 由 TuningStore.ResetToFactory 处理）
            if (TuningStore.Profile.FMaxBaselineMHz == 0 && snapshot.FMaxMHz > 0)
            {
                TuningProfile profile = TuningStore.Profile;
                profile.FMaxBaselineMHz = snapshot.FMaxMHz;
                TuningStore.Save(profile);
                LogService.Info($"已记录 FMax 出厂基线：{snapshot.FMaxMHz} MHz");
            }

            // 功耗墙：配置优先，没有配置则显示出厂基线
            FillPowerLimitBoxes();

            EnableTuningControls();
        }

        /// <summary>SMU 就绪后启用全部写入相关控件（探测 / 读取配置后共用）。</summary>
        private void EnableTuningControls()
        {
            // 写入进行中不要重新启用：写入完成后 RefreshAfterWriteAsync 会再走一次 ApplySnapshot，
            // 若这里无条件置 true，闸门会在 finally 之前就被绕过（Z-6 补全）。
            bool enabled = !_busy;
            ApplyAllButton.IsEnabled = enabled;
            ApplySaveButton.IsEnabled = enabled;
            ResetButton.IsEnabled = enabled;
            FMaxBox.IsEnabled = enabled;
            FmaxApplyButton.IsEnabled = enabled;

            // 功耗墙：三个按钮里只有「恢复默认」依赖基线，没有基线就置灰，
            // 而不是点了之后再弹「没有基线」—— 一个已知不可用的动作不该可点。
            PowerPptBox.IsEnabled = enabled;
            PowerTdcBox.IsEnabled = enabled;
            PowerEdcBox.IsEnabled = enabled;
            PowerTctlBox.IsEnabled = enabled;
            PowerApplyButton.IsEnabled = enabled;
            PowerRecaptureButton.IsEnabled = enabled;

            PowerBaseline? baseline = PowerBaselineStore.Current;
            PowerRestoreButton.IsEnabled = enabled && baseline != null && baseline.IsUsable;
        }

        /// <summary>写入期间统一禁用/恢复全部可编辑的调校控件（Z-6：防止重复点击造成并发下发）。
        ///
        /// 不只按钮：FMaxBox 与各核心 CO 输入框也要锁。它们是写入的「输入源」，
        /// 写入过程中改值会让下一次刷新回填成新值，用户会以为"我刚才改的没生效"。</summary>
        private void SetWriteButtonsEnabled(bool enabled)
        {
            SaveConfigButton.IsEnabled = enabled;
            LoadConfigButton.IsEnabled = enabled;
            ApplyAllButton.IsEnabled = enabled;
            ApplySaveButton.IsEnabled = enabled;
            ResetButton.IsEnabled = enabled;
            FmaxApplyButton.IsEnabled = enabled;
            FMaxBox.IsEnabled = enabled;

            // 功耗墙同步上锁：写入期间改输入框会让回读结果与用户预期错位
            PowerPptBox.IsEnabled = enabled;
            PowerTdcBox.IsEnabled = enabled;
            PowerEdcBox.IsEnabled = enabled;
            PowerTctlBox.IsEnabled = enabled;
            PowerApplyButton.IsEnabled = enabled;
            PowerRestoreButton.IsEnabled = enabled;
            PowerRecaptureButton.IsEnabled = enabled;

            // 核心输入框本身也锁上：写入期间手改某个核心值，会让回填结果与用户预期错位
            foreach (TextBox box in CoreBoxes)
                box.IsEnabled = enabled;
        }

        // ---------- 控件交互 ----------

        // 全核负压输入框与「填充到全核」按钮已移除，FillAllButton_Click 一并删除。
        // TODO(UI 重构)：参考朋友的软件界面重做 Curve Optimizer 区域，
        // 届时若恢复批量填入，再用滑杆或步进器实现，不再用单个裸输入框。

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            int count = 0;
            foreach (TextBox box in CoreBoxes)
            {
                if (box.IsEnabled)
                {
                    box.Text = "0";
                    count++;
                }
            }

            LogService.Info($"已恢复 {count} 个核心为默认 CO 值 0（未下发，点击“应用”生效）");
        }

        /// <summary>
        /// 从界面收集每个核心的 CO 值（允许 -50 ~ +50，负降压 / 正加压）。
        /// 仅拒绝“不是数字”的输入；区间内一律放行，不做安全拦截。
        /// 返回 null 表示存在非数字输入（已弹窗提示）。
        /// </summary>
        private int[]? CollectCoreMargins(out string? rejectDetail)
        {
            rejectDetail = null;
            int[] margins = new int[CoreBoxes.Count];
            System.Text.StringBuilder problems = new();

            for (int i = 0; i < CoreBoxes.Count; i++)
            {
                TextBox box = CoreBoxes[i];

                if (!box.IsEnabled)
                {
                    margins[i] = int.MinValue; // 跳过不可用的核心（熔断/读取失败）
                    continue;
                }

                if (!int.TryParse(box.Text, out int value))
                {
                    problems.AppendLine($"· 核心 {i}：不是有效整数（“{box.Text}”）");
                    continue;
                }

                // 允许 [-50, +50]，仅约束输入合法性
                value = Math.Clamp(value, CoMin, CoMax);
                box.Text = value.ToString();
                margins[i] = value;
            }

            if (problems.Length > 0)
            {
                rejectDetail = "以下输入不是有效整数，请修正后重试（未下发到 SMU）：\n\n" + problems;
                return null;
            }

            return margins;
        }

        /// <summary>
        /// 读取并校验 FMax 输入；失败返回 null（已弹窗）。
        /// 越界时不再拦截，而是自动收敛到边界值（信息提示，非警告拦截）后继续。
        /// </summary>
        private uint? CollectFMax()
        {
            if (!uint.TryParse(FMaxBox.Text, out uint fmax))
            {
                DarkDialog.ShowWarning($"FMax“{FMaxBox.Text}”不是有效的频率值。", "ZenForge · FMax 输入非法");
                return null;
            }

            if (fmax > FMaxMax)
            {
                fmax = FMaxMax;
                FMaxBox.Text = fmax.ToString();
                DarkDialog.Show($"FMax 已自动调整为 {FMaxMax} MHz（超出上限）", "ZenForge · FMax 收敛");
            }
            else if (fmax < FMaxMin)
            {
                fmax = FMaxMin;
                FMaxBox.Text = fmax.ToString();
                DarkDialog.Show($"FMax 已自动调整为 {FMaxMin} MHz（低于下限）", "ZenForge · FMax 收敛");
            }

            return fmax;
        }

        // ---------- 入口 1：CO 区「应用」——只写 CO ----------

        private void ApplySaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            // 仅校验 CO；绝对不读取 FMax 输入框、不写 FMax
            int[]? margins = CollectCoreMargins(out string? coReject);
            if (coReject != null)
            {
                DarkDialog.ShowWarning(coReject, "ZenForge · CO 输入非法");
                return;
            }

            _ = ApplyCoOnlyAsync(margins!);
        }

        private async System.Threading.Tasks.Task ApplyCoOnlyAsync(int[] margins)
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Writing, "正在写入 Curve Optimizer");

            SmuWriteResult co = new();
            try
            {
                co = await System.Threading.Tasks.Task.Run(() =>
                {
                    SmuReader reader = SmuReader.Shared;
                    return reader.WriteCurveOptimizer(margins); // 只写 CO，绝不写 FMax
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"CO 写入线程异常: {ex}");
                DarkDialog.ShowError(
                    $"写入过程中发生异常：{ex.Message}\n\n详细信息已写入日志。",
                    "写入异常");
                return;
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            ProcessCoResult(co, margins);
        }

        private void ProcessCoResult(SmuWriteResult co, int[] margins)
        {
            if (!co.AllSucceeded)
            {
                if (!string.IsNullOrEmpty(co.Fatal))
                    LogService.Error($"CO 写入致命错误：{co.Fatal}");
                if (co.WriteFailed.Count > 0)
                    LogService.Error($"CO 写入被拒核心：{string.Join(", ", co.WriteFailed)}");
                if (co.VerifyFailed.Count > 0)
                    LogService.Error($"CO 回读不一致核心：{string.Join(", ", co.VerifyFailed)}");

                SetSmuState(SmuUiState.Failed, "CO 写入存在失败");
                System.Text.StringBuilder msg = new("Curve Optimizer 部分写入失败，未保存配置。详细如下：\n\n");
                if (!string.IsNullOrEmpty(co.Fatal)) msg.AppendLine($"· {co.Fatal}");
                if (co.WriteFailed.Count > 0) msg.AppendLine($"· CO 被拒核心：{string.Join(", ", co.WriteFailed)}");
                if (co.VerifyFailed.Count > 0) msg.AppendLine($"· CO 回读不一致核心：{string.Join(", ", co.VerifyFailed)}");
                msg.AppendLine("\n已写入成功的部分仍生效（SMU 实时生效），但未落盘以免配置不一致。");
                DarkDialog.ShowError(msg.ToString(), "CO 写入失败");
                return;
            }

            // 成功：仅保存 CO，保留配置中已有的 FMax
            TuningProfile profile = TuningStore.Profile;
            profile.CurveOptimizer = margins.Where(v => v != int.MinValue).ToArray();
            TuningStore.Save(profile);

            LogService.Info($"CO 全部 {co.Applied.Count} 个核心写入并回读一致（FMax 未改动）");
            SetSmuState(SmuUiState.Applied, "CO 已生效 · 已保存");
            DarkDialog.ShowSuccess(
                $"Curve Optimizer 已应用并保存。\n\n" +
                $"CO：{profile.CurveOptimizer.Length} 个核心写入并回读一致\n" +
                $"（FMax 未改动，未写入 SMU）",
                "CO 应用成功");

            _ = RefreshAfterWriteAsync();
        }

        // ---------- 入口 2：顶部「全部应用并保存」——CO + FMax 一起写 ----------

        private void ApplyAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            int[]? margins = CollectCoreMargins(out string? coReject);
            if (coReject != null)
            {
                DarkDialog.ShowWarning(coReject, "ZenForge · CO 输入非法");
                return;
            }

            uint? fmax = CollectFMax();
            if (fmax == null)
                return;

            // 版式与 NvForge 的 V/F 确认窗保持一致：
            // 标题句 + 空行 + 每项一行（不加「·」前缀）+ 空行 + 一句后果。
            // CO 偏移给大了确实会黑屏/掉电重启，所以风险句保留，且只说这一次。
            bool confirmed = DarkDialog.Confirm(
                $"即将应用以下设置：\n\n" +
                $"Curve Optimizer：{margins!.Count(v => v != int.MinValue)} 个核心\n" +
                $"FMax：{fmax} MHz\n\n" +
                $"若发生黑屏或闪退，重启电脑即可复原。",
                "全部应用");

            if (!confirmed)
                return;

            _ = ApplyAllAsync(margins!, fmax.Value);
        }

        private async System.Threading.Tasks.Task ApplyAllAsync(int[] margins, uint fmax)
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Writing, "正在写入 CO + FMax");

            (SmuWriteResult co, SmuFMaxResult fm) result = (new SmuWriteResult(), new SmuFMaxResult());
            try
            {
                result = await System.Threading.Tasks.Task.Run(() =>
                {
                    SmuReader reader = SmuReader.Shared;
                    SmuWriteResult co = reader.WriteCurveOptimizer(margins);
                    SmuFMaxResult fm = reader.WriteFMax(fmax);
                    return (co, fm);
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"写入线程异常: {ex}");
                DarkDialog.ShowError(
                    $"写入过程中发生异常：{ex.Message}\n\n详细信息已写入日志。",
                    "写入异常");
                return;
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            ProcessAllResult(result.co, result.fm, margins, fmax);
        }

        private void ProcessAllResult(SmuWriteResult co, SmuFMaxResult fm, int[] margins, uint fmax)
        {
            bool coOk = co.AllSucceeded;
            bool fmOk = fm.AllSucceeded;

            if (!coOk)
            {
                if (!string.IsNullOrEmpty(co.Fatal))
                    LogService.Error($"CO 写入致命错误：{co.Fatal}");
                if (co.WriteFailed.Count > 0)
                    LogService.Error($"CO 写入被拒核心：{string.Join(", ", co.WriteFailed)}");
                if (co.VerifyFailed.Count > 0)
                    LogService.Error($"CO 回读不一致核心：{string.Join(", ", co.VerifyFailed)}");
            }
            else
            {
                LogService.Info($"CO 全部 {co.Applied.Count} 个核心写入并回读一致");
            }

            if (!fmOk)
            {
                LogService.Error(string.IsNullOrEmpty(fm.Fatal)
                    ? $"FMax 回读不一致：请求 {fm.Requested}，SMU 接受 {fm.SmuAccepted}，回读 {fm.ReadBack}"
                    : $"FMax 写入致命错误：{fm.Fatal}");
            }
            else
            {
                LogService.Info($"FMax 写入并回读一致：{fm.ReadBack} MHz");
            }

            if (coOk && fmOk)
            {
                // 就地修改，不新建：新建 TuningProfile 会把已保存的功耗墙一起抹掉
                TuningProfile profile = TuningStore.Profile;
                profile.CurveOptimizer = margins.Where(v => v != int.MinValue).ToArray();
                profile.FMaxMHz = fmax;
                TuningStore.Save(profile);
                LogService.Info("调校已全部应用并保存到本地配置（tuning.json）");

                SetSmuState(SmuUiState.Applied, "CO + FMax 已生效 · 已保存");
                DarkDialog.ShowSuccess(
                    $"全部应用成功。\n\n" +
                    $"CO：{profile.CurveOptimizer.Length} 个核心写入并回读一致\n" +
                    $"FMax：{fmax} MHz 回读一致",
                    "全部应用成功");

                _ = RefreshAfterWriteAsync();
            }
            else
            {
                SetSmuState(SmuUiState.Failed, "写入存在失败");
                System.Text.StringBuilder msg = new("部分写入失败，未保存配置。详细如下：\n\n");
                if (!coOk)
                {
                    if (!string.IsNullOrEmpty(co.Fatal)) msg.AppendLine($"· {co.Fatal}");
                    if (co.WriteFailed.Count > 0) msg.AppendLine($"· CO 被拒核心：{string.Join(", ", co.WriteFailed)}");
                    if (co.VerifyFailed.Count > 0) msg.AppendLine($"· CO 回读不一致核心：{string.Join(", ", co.VerifyFailed)}");
                }
                if (!fmOk)
                {
                    msg.AppendLine(fm.Fatal != null
                        ? $"· FMax：{fm.Fatal}"
                        : $"· FMax：回读不一致（请求 {fm.Requested}，回读 {fm.ReadBack}）");
                }
                msg.AppendLine("\n已写入成功的部分仍生效（SMU 实时生效），但未落盘以免配置不一致。");
                DarkDialog.ShowError(msg.ToString(), "写入失败");
            }
        }

        private async System.Threading.Tasks.Task RefreshAfterWriteAsync()
        {
            try
            {
                SmuSnapshot? snapshot = await System.Threading.Tasks.Task.Run(() =>
                {
                    SmuReader reader = SmuReader.Shared;
                    return reader.IsAvailable ? reader.ReadSnapshot() : null;
                });
                if (snapshot != null && snapshot.Success)
                    Dispatcher.Invoke(() => ApplySnapshot(snapshot));
            }
            catch
            {
                // 刷新失败不影响已生效的写入
            }
        }

        private void FmaxApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            uint? fmax = CollectFMax();
            if (fmax == null)
                return;

            _ = ApplyFMaxOnlyAsync(fmax.Value);
        }

        /// <summary>FMax 区「应用」：只写 FMax，绝不碰 CO。</summary>
        private async System.Threading.Tasks.Task ApplyFMaxOnlyAsync(uint fmax)
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Writing, "正在写入 FMax");

            SmuFMaxResult fm = new();
            try
            {
                fm = await System.Threading.Tasks.Task.Run(() =>
                    SmuReader.Shared.WriteFMax(fmax)); // 只写 FMax，绝不写 CO
            }
            catch (Exception ex)
            {
                LogService.Error($"FMax 写入线程异常: {ex}");
                DarkDialog.ShowError(
                    $"写入过程中发生异常：{ex.Message}\n\n详细信息已写入日志。",
                    "写入异常");
                return;
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            ProcessFmaxResult(fm, fmax);
        }

        private void ProcessFmaxResult(SmuFMaxResult fm, uint fmax)
        {
            if (fm.AllSucceeded)
            {
                // 仅更新 FMax 到配置，保留已有 CO 设定
                TuningProfile profile = TuningStore.Profile;
                profile.FMaxMHz = fmax;
                TuningStore.Save(profile);
                LogService.Info($"FMax 已应用并保存：{fmax} MHz（回读 {fm.ReadBack}，CO 未改动）");
                SetSmuState(SmuUiState.Applied, $"FMax {fm.ReadBack} MHz 已生效 · 已保存");
                DarkDialog.ShowSuccess(
                    $"FMax 已应用并回读一致：{fm.ReadBack} MHz\n（CO 未改动，未写入 SMU）",
                    "FMax 应用成功");
            }
            else
            {
                LogService.Error(fm.Fatal != null
                    ? $"FMax 写入失败：{fm.Fatal}"
                    : $"FMax 回读不一致：请求 {fm.Requested}，回读 {fm.ReadBack}");
                SetSmuState(SmuUiState.Failed, "FMax 写入失败");
                DarkDialog.ShowError(
                    fm.Fatal != null
                        ? $"FMax 写入失败：{fm.Fatal}"
                        : $"FMax 回读不一致（请求 {fmax}，回读 {fm.ReadBack}），未保存。",
                    "FMax 写入失败");
            }
        }

        // ---------- 功耗墙 ----------

        /// <summary>
        /// 回填四个输入框。
        ///
        /// 取值优先级：配置里保存的设定值 → 出厂基线 → 留空。
        /// 留空而不是填 0：0 在这个界面里是「用户想把墙设成 0」的歧义，
        /// 而空字符串只会被理解为「这一项不写」，不会误下发。
        /// </summary>
        private void FillPowerLimitBoxes()
        {
            TuningProfile profile = TuningStore.Profile;
            PowerBaseline? baseline = PowerBaselineStore.Current;

            PowerPptBox.Text = MilliToText(profile.PptMilliwatt, baseline?.PptMilliwatt ?? 0);
            PowerTdcBox.Text = MilliToText(profile.TdcMilliamp, baseline?.TdcMilliamp ?? 0);
            PowerEdcBox.Text = MilliToText(profile.EdcMilliamp, 0); // EDC 无出厂值
            PowerTctlBox.Text = CelsiusToText(profile.TctlCelsius, baseline?.TctlCelsius ?? 0);

            UpdateBaselineText();
        }

        /// <summary>mW / mA → 界面文本（W / A，最多一位小数）。</summary>
        private static string MilliToText(uint saved, int baseline)
        {
            uint raw = saved > 0 ? saved : (uint)Math.Max(0, baseline);
            return raw > 0 ? (raw / 1000.0).ToString("0.#") : string.Empty;
        }

        private static string CelsiusToText(uint saved, int baseline)
        {
            uint raw = saved > 0 ? saved : (uint)Math.Max(0, baseline);
            return raw > 0 ? raw.ToString() : string.Empty;
        }

        private void UpdateBaselineText()
        {
            PowerBaseline? baseline = PowerBaselineStore.Current;
            if (baseline == null || !baseline.IsUsable)
            {
                PowerBaselineText.Text = "出厂基线未采集。恢复默认不可用，可点「现读并覆盖」重新采集。";
                return;
            }

            System.Collections.Generic.List<string> parts = new();
            if (baseline.HasPpt) parts.Add($"PPT {baseline.PptMilliwatt / 1000.0:0.#} W");
            if (baseline.HasTdc) parts.Add($"TDC {baseline.TdcMilliamp / 1000.0:0.#} A");
            if (baseline.HasTctl) parts.Add($"温度墙 {baseline.TctlCelsius} ℃");

            string body = parts.Count > 0 ? string.Join(" / ", parts) : "无可用项";
            string time = baseline.CapturedUtc == default
                ? string.Empty
                : $" · {baseline.CapturedUtc.ToLocalTime():MM-dd HH:mm}";

            // EDC 单独说明，不混在数值里 —— 它不是「读不到」，是这个平台结构上就没有。
            PowerBaselineText.Text = $"出厂基线：{body}{time} · EDC 无出厂值，不参与恢复";
        }

        /// <summary>
        /// 从界面收集四项墙值。空输入 = 该项不写；非法输入 = 整体拒绝（已由调用方弹窗）。
        /// 只拦明显不可能的数值，不做安全限制。
        /// </summary>
        private PowerLimitSet? CollectPowerLimits(out string? rejectDetail)
        {
            rejectDetail = null;
            PowerLimitSet set = new();
            System.Text.StringBuilder problems = new();

            if (TryReadMilli(PowerPptBox, PowerLimitKind.Ppt, out uint ppt, problems))
                set.PptMilliwatt = ppt;

            if (TryReadMilli(PowerTdcBox, PowerLimitKind.Tdc, out uint tdc, problems))
                set.TdcMilliamp = tdc;

            if (TryReadMilli(PowerEdcBox, PowerLimitKind.Edc, out uint edc, problems))
                set.EdcMilliamp = edc;

            if (TryReadCelsius(PowerTctlBox, out uint tctl, problems))
                set.TctlCelsius = tctl;

            if (problems.Length > 0)
            {
                rejectDetail = "以下输入不合法，未下发到 SMU：\n\n" + problems;
                return null;
            }

            if (set.IsEmpty)
            {
                rejectDetail = "四项均为空，没有可写入的内容。";
                return null;
            }

            return set;
        }

        /// <summary>解析 W / A 输入并换算成 mW / mA。空输入返回 false 且不记问题。</summary>
        private static bool TryReadMilli(
            TextBox box, PowerLimitKind kind, out uint milli, System.Text.StringBuilder problems)
        {
            milli = 0;
            string text = box.Text.Trim();
            if (text.Length == 0)
                return false;

            string name = PowerLimitOutcome.NameOf(kind);
            int min = kind == PowerLimitKind.Ppt ? PowerLimitBounds.PptMinWatt : PowerLimitBounds.CurrentMinAmp;
            int max = kind == PowerLimitKind.Ppt ? PowerLimitBounds.PptMaxWatt : PowerLimitBounds.CurrentMaxAmp;
            string unit = kind == PowerLimitKind.Ppt ? "W" : "A";

            if (!double.TryParse(text, out double value))
            {
                problems.AppendLine($"· {name}：不是有效数值（“{text}”）");
                return false;
            }

            if (value < min || value > max)
            {
                problems.AppendLine($"· {name}：需在 {min} ~ {max} {unit} 之间（当前 {text}）");
                return false;
            }

            // 四舍五入到整数毫单位，避免 142.4 W 这种输入变成 142400 mW 之外的多余精度
            milli = (uint)Math.Round(value * 1000.0);
            return true;
        }

        private static bool TryReadCelsius(TextBox box, out uint celsius, System.Text.StringBuilder problems)
        {
            celsius = 0;
            string text = box.Text.Trim();
            if (text.Length == 0)
                return false;

            if (!int.TryParse(text, out int value))
            {
                problems.AppendLine($"· 温度墙：不是有效整数（“{text}”）");
                return false;
            }

            if (value < PowerLimitBounds.TctlMinCelsius || value > PowerLimitBounds.TctlMaxCelsius)
            {
                problems.AppendLine(
                    $"· 温度墙：需在 {PowerLimitBounds.TctlMinCelsius} ~ {PowerLimitBounds.TctlMaxCelsius} ℃ 之间（当前 {text}）");
                return false;
            }

            celsius = (uint)value;
            return true;
        }

        private void PowerApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            PowerLimitSet? set = CollectPowerLimits(out string? reject);
            if (set == null)
            {
                DarkDialog.ShowWarning(reject!, "ZenForge · 功耗墙输入非法");
                return;
            }

            _ = ApplyPowerLimitsAsync(set);
        }

        private async System.Threading.Tasks.Task ApplyPowerLimitsAsync(PowerLimitSet set)
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Writing, "正在写入功耗墙");

            PowerLimitApplyResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(
                    () => PowerLimitService.Apply(SmuReader.Shared, set));
            }
            catch (Exception ex)
            {
                LogService.Error($"功耗墙写入线程异常: {ex}");
                DarkDialog.ShowError(
                    $"写入过程中发生异常：{ex.Message}\n\n详细信息已写入日志。",
                    "写入异常");
                return;
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            ProcessPowerResult(result, isRestore: false);
        }

        private void ProcessPowerResult(PowerLimitApplyResult result, bool isRestore)
        {
            // 前置条件失败优先提示：这是唯一需要用户去 BIOS 动手的情况，
            // 和其它「重试可能就好了」的失败混在一起说等于没说。
            string? hint = null;
            foreach (PowerLimitOutcome item in result.Items)
            {
                if (item.BlockedByPrereq)
                {
                    hint = $"需在 BIOS 开启 PBO 后再试（{PowerLimitOutcome.NameOf(item.Kind)} 被拒绝）";
                    break;
                }
            }

            PowerHintText.Text = hint ?? string.Empty;
            PowerHintText.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;

            if (result.AllSucceeded)
            {
                // 成功才落盘。部分成功也落盘 —— 逐项独立的前提是「成功的项确实生效了」，
                // 不落盘会导致下次自启把它们当成没设过，反而丢掉用户已经调好的值。
                if (!isRestore)
                    PersistPowerLimits(result);

                SetSmuState(SmuUiState.Applied,
                    isRestore ? "功耗墙已恢复出厂" : "功耗墙已生效 · 已保存");

                System.Text.StringBuilder ok = new(isRestore ? "已恢复出厂值。\n\n" : "全部写入成功。\n\n");
                foreach (PowerLimitOutcome item in result.Items)
                    ok.AppendLine($"{PowerLimitOutcome.NameOf(item.Kind)}：{item.Message}");
                DarkDialog.ShowSuccess(ok.ToString(),
                    isRestore ? "恢复默认" : "功耗墙应用成功");

                _ = RefreshAfterWriteAsync();
                return;
            }

            if (isRestore && result.Items.Count == 0)
            {
                SetSmuState(SmuUiState.Failed, "无可恢复项");
                DarkDialog.ShowWarning(
                    "没有可用的出厂基线，无法恢复。\n\n可点「现读并覆盖」重新采集一次。",
                    "恢复默认");
                return;
            }

            // 部分或全部失败：成功的项照常落盘，失败项逐条列出。
            if (result.AnySucceeded && !isRestore)
                PersistPowerLimits(result);

            SetSmuState(SmuUiState.Failed, "功耗墙存在失败项");

            System.Text.StringBuilder msg = new(
                result.AnySucceeded
                    ? "部分写入成功，失败项如下：\n\n"
                    : "写入失败，失败项如下：\n\n");

            foreach (PowerLimitOutcome item in result.Items)
            {
                if (item.Ok) continue;
                msg.AppendLine($"{PowerLimitOutcome.NameOf(item.Kind)}：{item.Message}");
            }

            if (hint != null)
                msg.AppendLine($"\n{hint}");

            msg.AppendLine("\n已成功的项已生效并保存；失败项未写入。");
            DarkDialog.ShowError(msg.ToString(), isRestore ? "恢复默认" : "功耗墙写入失败");

            _ = RefreshAfterWriteAsync();
        }

        /// <summary>把成功的项写进 tuning.json，保留 CO / FMax 不动。</summary>
        private void PersistPowerLimits(PowerLimitApplyResult result)
        {
            TuningProfile profile = TuningStore.Profile;

            foreach (PowerLimitOutcome item in result.Items)
            {
                if (!item.Ok) continue;
                switch (item.Kind)
                {
                    case PowerLimitKind.Ppt: profile.PptMilliwatt = item.Requested; break;
                    case PowerLimitKind.Tdc: profile.TdcMilliamp = item.Requested; break;
                    case PowerLimitKind.Edc: profile.EdcMilliamp = item.Requested; break;
                    case PowerLimitKind.Tctl: profile.TctlCelsius = item.Requested; break;
                }
            }

            TuningStore.Save(profile);
            LogService.Info("功耗墙设定已保存到本地配置（tuning.json）");
        }

        private void PowerRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            PowerBaseline? baseline = PowerBaselineStore.Current;
            if (baseline == null || !baseline.IsUsable)
            {
                DarkDialog.ShowWarning(
                    "没有可用的出厂基线。\n\n可点「现读并覆盖」重新采集一次。",
                    "恢复默认");
                return;
            }

            System.Text.StringBuilder desc = new("即将把这四项写回出厂值：\n\n");
            if (baseline.HasPpt) desc.AppendLine($"PPT：{baseline.PptMilliwatt / 1000.0:0.#} W");
            if (baseline.HasTdc) desc.AppendLine($"TDC：{baseline.TdcMilliamp / 1000.0:0.#} A");
            if (baseline.HasTctl) desc.AppendLine($"温度墙：{baseline.TctlCelsius} ℃");
            desc.AppendLine("\nEDC 无出厂值，不参与恢复。");

            if (!DarkDialog.Confirm(desc.ToString(), "恢复功耗墙默认"))
                return;

            _ = RestorePowerLimitsAsync(baseline);
        }

        private async System.Threading.Tasks.Task RestorePowerLimitsAsync(PowerBaseline baseline)
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Writing, "正在恢复功耗墙出厂值");

            PowerLimitApplyResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(
                    () => PowerLimitService.RestoreDefaults(SmuReader.Shared, baseline));
            }
            catch (Exception ex)
            {
                LogService.Error($"恢复功耗墙异常: {ex}");
                DarkDialog.ShowError($"恢复过程中发生异常：{ex.Message}", "恢复异常");
                return;
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            if (result.AllSucceeded && result.Items.Count > 0)
            {
                // 恢复出厂后，配置里的自定义墙值必须一并清掉，
                // 否则下次自启又会把用户的自定义值写回去，恢复等于白做。
                TuningProfile profile = TuningStore.Profile;
                profile.PptMilliwatt = 0;
                profile.TdcMilliamp = 0;
                profile.EdcMilliamp = 0;
                profile.TctlCelsius = 0;
                TuningStore.Save(profile);
                LogService.Info("功耗墙已恢复出厂，配置中的自定义墙值已清零");
            }

            ProcessPowerResult(result, isRestore: true);
        }

        /// <summary>
        /// 「现读并覆盖」：重新从 SMU 采集基线并写盘，覆盖旧文件。
        ///
        /// 这是唯一允许推翻既有基线的入口。自动重取是不行的 ——
        /// 一旦用户改过墙值，某些固件会让融断读取返回当前生效值，
        /// 自动重取等于把污染固化成「出厂值」。
        /// </summary>
        private void PowerRecaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            PowerBaseline? old = PowerBaselineStore.Current;
            string warn = old == null
                ? "将重新从 SMU 采集出厂基线并覆盖现有文件。"
                : "将重新采集并覆盖现有基线。\n\n" +
                  "若当前已改过功耗墙，部分固件会让融断读取返回当前生效值而非出厂值，\n" +
                  "采集结果可能不是真正的出厂值。确认继续？";

            if (!DarkDialog.Confirm(warn, "现读并覆盖"))
                return;

            _ = RecaptureBaselineAsync();
        }

        private async System.Threading.Tasks.Task RecaptureBaselineAsync()
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Writing, "正在重新采集出厂基线");

            PowerBaseline? fresh = null;
            try
            {
                fresh = await System.Threading.Tasks.Task.Run(
                    () => SmuReader.Shared.CaptureBaseline("refresh"));
            }
            catch (Exception ex)
            {
                LogService.Error($"重新采集基线异常: {ex}");
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            if (fresh == null || !fresh.IsUsable)
            {
                SetSmuState(SmuUiState.Failed, "基线采集失败");
                DarkDialog.ShowError(
                    "未能采集到可用的出厂基线。\n\n可能原因：该平台不提供融断值，或 SMU 未就绪。详细信息见日志。",
                    "采集失败");
                return;
            }

            PowerBaselineStore.Save(fresh);
            UpdateBaselineText();

            SetSmuState(SmuUiState.Applied, "基线已重新采集");
            System.Text.StringBuilder msg = new("出厂基线已重新采集并保存：\n\n");
            if (fresh.HasPpt) msg.AppendLine($"PPT：{fresh.PptMilliwatt / 1000.0:0.#} W");
            if (fresh.HasTdc) msg.AppendLine($"TDC：{fresh.TdcMilliamp / 1000.0:0.#} A");
            if (fresh.HasTctl) msg.AppendLine($"温度墙：{fresh.TctlCelsius} ℃");
            msg.AppendLine("\nEDC 无出厂值，不参与恢复。");
            DarkDialog.ShowSuccess(msg.ToString(), "采集完成");

            // 基线变了，恢复按钮的可用性要跟着变
            EnableTuningControls();
        }

        // ---------- 新增：一键读取 / 配置存档 ----------

        /// <summary>
        /// 「保存当前配置」：仅把界面上的 CO（收集 CoreBoxes）+ FMax（FMaxBox）
        /// 写入 tuning.json，不写 SMU。
        /// </summary>
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            int[]? margins = CollectCoreMargins(out string? coReject);
            if (coReject != null)
            {
                DarkDialog.ShowWarning(coReject, "ZenForge · CO 输入非法");
                return;
            }

            uint? fmax = CollectFMax();
            if (fmax == null)
                return;

            // 功耗墙一并落盘（空输入 = 不动该项，非法输入直接拒绝保存）
            PowerLimitSet? power = CollectPowerLimits(out string? powerReject);
            if (powerReject != null)
            {
                DarkDialog.ShowWarning(powerReject, "ZenForge · 功耗墙输入非法");
                return;
            }

            TuningProfile profile = TuningStore.Profile;
            profile.CurveOptimizer = margins!.Where(v => v != int.MinValue).ToArray();
            profile.FMaxMHz = fmax.Value;

            if (power != null)
            {
                if (power.PptMilliwatt.HasValue) profile.PptMilliwatt = power.PptMilliwatt.Value;
                if (power.TdcMilliamp.HasValue) profile.TdcMilliamp = power.TdcMilliamp.Value;
                if (power.EdcMilliamp.HasValue) profile.EdcMilliamp = power.EdcMilliamp.Value;
                if (power.TctlCelsius.HasValue) profile.TctlCelsius = power.TctlCelsius.Value;
            }

            TuningStore.Save(profile);

            LogService.Info("保存当前配置到本地（仅落盘，未写 SMU）");
            DarkDialog.ShowSuccess("已保存当前配置到本地（未写入 SMU）。", "保存配置");
        }

        /// <summary>
        /// 「读取」：从 tuning.json 读出 CO / FMax 回填到界面输入框。
        /// 只回填，不下发 SMU（下发由「全部应用」负责）。
        /// </summary>
        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            TuningProfile profile = TuningStore.Profile;

            if (profile.FMaxMHz > 0)
                FMaxBox.Text = profile.FMaxMHz.ToString();

            int[] co = profile.CurveOptimizer ?? Array.Empty<int>();
            for (int i = 0; i < CoreBoxes.Count && i < co.Length; i++)
                CoreBoxes[i].Text = co[i].ToString();

            // 功耗墙同样只回填，不下发
            FillPowerLimitBoxes();

            // 回填后启用控件
            EnableTuningControls();

            LogService.Info("读取已保存配置并回填到界面（未写入 SMU）");
            DarkDialog.Show("已读取已保存配置并回填到界面（未写入 SMU）。", "读取配置");
        }


    }
}
