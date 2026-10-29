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
    ///
    /// 四个动作各有明确方向，互不重叠：
    ///   · 保存 —— 界面 → tuning.json，不碰 SMU
    ///   · 加载 —— tuning.json → 界面，不碰 SMU
    ///   · 刷新 —— SMU 现值 → 界面，不写盘
    ///   · 应用 —— 界面 → SMU，并落盘（唯一的写入口）
    ///
    /// CO 与 FMax 不再各有独立的应用按钮：两个区块一旦分开写，
    /// 就会出现「CO 生效了但 FMax 还是旧的」这种半套状态，配置与硬件对不上。
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

            EnableTuningControls();
        }

        /// <summary>SMU 就绪后启用全部写入相关控件（探测 / 读取配置后共用）。</summary>
        private void EnableTuningControls()
        {
            // 写入进行中不要重新启用：写入完成后 RefreshAfterWriteAsync 会再走一次 ApplySnapshot，
            // 若这里无条件置 true，闸门会在 finally 之前就被绕过（Z-6 补全）。
            bool enabled = !_busy;
            ApplyAllButton.IsEnabled = enabled;
            RefreshButton.IsEnabled = enabled;
            FMaxBox.IsEnabled = enabled;
        }

        /// <summary>写入期间统一禁用/恢复全部可编辑的调校控件（Z-6：防止重复点击造成并发下发）。
        ///
        /// 不只按钮：FMaxBox 与各核心 CO 输入框也要锁。它们是写入的「输入源」，
        /// 写入过程中改值会让下一次刷新回填成新值，用户会以为"我刚才改的没生效"。</summary>
        private void SetWriteButtonsEnabled(bool enabled)
        {
            SaveConfigButton.IsEnabled = enabled;
            LoadConfigButton.IsEnabled = enabled;
            RefreshButton.IsEnabled = enabled;
            ApplyAllButton.IsEnabled = enabled;
            FMaxBox.IsEnabled = enabled;

            // 核心输入框本身也锁上：写入期间手改某个核心值，会让回填结果与用户预期错位
            foreach (TextBox box in CoreBoxes)
                box.IsEnabled = enabled;
        }

        // ---------- 控件交互 ----------

        // 全核负压输入框与「填充到全核」按钮已移除，FillAllButton_Click 一并删除。
        // TODO(UI 重构)：参考朋友的软件界面重做 Curve Optimizer 区域，
        // 届时若恢复批量填入，再用滑杆或步进器实现，不再用单个裸输入框。

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

        // ---------- 唯一的写入口：顶部「应用」——CO + FMax 一起写 ----------

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
                "应用");

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
                LogService.Info("调校已应用并保存到本地配置（tuning.json）");

                SetSmuState(SmuUiState.Applied, "CO + FMax 已生效 · 已保存");
                DarkDialog.ShowSuccess(
                    $"应用成功。\n\n" +
                    $"CO：{profile.CurveOptimizer.Length} 个核心写入并回读一致\n" +
                    $"FMax：{fmax} MHz 回读一致",
                    "应用成功");

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

            TuningProfile profile = TuningStore.Profile;
            profile.CurveOptimizer = margins!.Where(v => v != int.MinValue).ToArray();
            profile.FMaxMHz = fmax.Value;

            TuningStore.Save(profile);

            LogService.Info("保存当前配置到本地（仅落盘，未写 SMU）");
            DarkDialog.ShowSuccess("已保存当前配置到本地（未写入 SMU）。", "保存配置");
        }

        /// <summary>
        /// 「加载」：从 tuning.json 读出 CO / FMax 回填到界面输入框。
        /// 只回填，不下发 SMU（下发由顶部「应用」负责）。
        ///
        /// 和「刷新」的区别：加载读的是本地存档，刷新读的是 SMU 当前生效值。
        /// </summary>
        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            TuningProfile profile = TuningStore.Profile;

            if (profile.FMaxMHz > 0)
                FMaxBox.Text = profile.FMaxMHz.ToString();

            int[] co = profile.CurveOptimizer ?? Array.Empty<int>();
            for (int i = 0; i < CoreBoxes.Count && i < co.Length; i++)
                CoreBoxes[i].Text = co[i].ToString();

            // 回填后启用控件
            EnableTuningControls();

            LogService.Info("已加载本地配置并回填到界面（未写入 SMU）");
            DarkDialog.Show("已加载本地配置并回填到界面（未写入 SMU）。", "加载配置");
        }

        /// <summary>
        /// 「刷新」：从 SMU 现读当前生效的 CO / FMax，回填到界面。
        /// 只读，不写盘、不下发。
        ///
        /// 和「加载」的区别：加载读本地 tuning.json，可能早就和硬件不一致；
        /// 刷新读的是硬件此刻的真实值，用来确认「我上一次应用的到底生效了没有」。
        /// </summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            _ = RefreshFromSmuAsync();
        }

        private async System.Threading.Tasks.Task RefreshFromSmuAsync()
        {
            _busy = true;
            SetWriteButtonsEnabled(false);
            SetSmuState(SmuUiState.Probing, "手动刷新：从 SMU 现读");

            SmuSnapshot? snapshot = null;
            string? error = null;

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
                error = ex.Message;
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            if (snapshot == null || !snapshot.Success)
            {
                string reason = snapshot?.Error ?? error ?? "未知原因";
                LogService.Error($"手动刷新失败：{reason}");
                SetSmuState(SmuUiState.Failed, reason);
                DarkDialog.ShowWarning(
                    "刷新失败，请确认已安装 PawnIO 驱动并以管理员身份运行。\n\n详细信息已写入日志（设置 → 日志）。",
                    "刷新失败");
                return;
            }

            ApplySnapshot(snapshot);

            LogService.Info("已按 SMU 现值刷新界面（未写入 SMU、未改本地配置）");
        }
    }
}
