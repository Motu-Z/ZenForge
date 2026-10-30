using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 调校页。
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
        /// <summary>本页在路由中的 key，与 MainWindow 侧栏的 Tag 一致。</summary>
        public const string PageKey = "tuning";

        // 与 SmuReader 的输入合法性边界保持一致
        private const int CoMin = SmuReader.CoMin;
        private const int CoMax = SmuReader.CoMax;
        private const uint FMaxMin = SmuReader.FMaxMin;
        private const uint FMaxMax = SmuReader.FMaxMax;

        private bool _busy; // 防止并发点击重复下发

        /// <summary>
        /// 电源计划策略下拉框的「程序正在回填」闸门。
        ///
        /// 回填时 SelectedIndex 会触发 SelectionChanged，如果不挡住，进页面就会
        /// 把刚读到的值立刻再写回去一遍 —— 看似无害，实则每次切页都对电源方案
        /// 做四次写操作并 PowerSetActiveScheme，还可能在读失败时把用户的设置
        /// 覆写成默认档。所有「代码改选中项」的地方都必须先置 true。
        /// </summary>
        private bool _suppressPlanEvent;

        /// <summary>当前实际档位（用户选择成功后才更新，用于失败时回滚 UI）。null = 系统值对不上任何档位。</summary>
        private PowerPlanProfile? _planCurrent = PowerPlanProfile.Balanced;

        public TuningPage()
        {
            InitializeComponent();

            // 核心输入框不在构造时生成：不同 CPU 的核心数 / CCD 数不同，
            // 必须等 SMU 只读探测拿到快照后，由 ApplySnapshot 按真实核心数
            // 调用 BuildCoreGrid 延迟构建（见 Z-2）。
            Loaded += async (_, _) =>
            {
                await ProbeSmuAsync();

                // 电源计划策略与 SMU 探测互不依赖：SMU 失败（非 AMD / 未装驱动）时
                // 电源策略照样可读，所以单独一趟，不能塞进 ApplySnapshot。
                LoadPowerPlanPolicy();

                // 升频模式与核心停放同样是纯 Windows 电源管理读取，
                // 与 SMU 无关，各自独立加载；某一项读不到不影响其他两项。
                LoadBoostMode();
                LoadCoreParking();
            };
        }

        /// <summary>
        /// 按真实核心数构建 CO 输入框网格（Z-2 修复：原版写死 2 CCD × 8 核 = 16 个框，
        /// 非 16 核 CPU 会静默丢核或留空框）。
        ///
        /// 版式说明（v12）：容器是 <c>UniformGrid Columns=8</c>，不再按 CCD 分行、
        /// 不再有 CCD 标签。所有格子平铺进去，填充顺序严格等于核心号 0..n-1，
        /// 这样 <see cref="CoreBoxes"/> 的下标就是核心号，回填与收集都靠它。
        /// UniformGrid 没有 RowSpacing，行间距只能靠格子自身的 Margin 制造：
        /// 横向留 6、纵向留 8，最后一列/最后一行不留（避免外沿多出一截）。
        /// 每 8 个核插一个小的 CCD 分隔（由提示文字承担，网格本身不画线）。
        /// </summary>
        private void BuildCoreGrid(int totalCores, int coresPerCcd)
        {
            CoreGrid.Children.Clear();
            CoreBoxes.Clear();

            // 显式取 Style 而非依赖隐式查找：隐式样式在 Page 层可能被局部覆盖而回落系统默认白底；
            // 只赋 Style，不设局部 Background/Foreground/BorderBrush（局部值会压掉「聚焦青边」触发器）。
            Style? coreBoxStyle = TryFindResource("TuningBoxStyle") as Style;

            // L3 数值字体（等宽 / 16 / Bold）。走资源而不是写字面量：
            // 将来换字体栈只改 Themes/Typography.xaml 一处。
            // 等宽不是审美选择 —— CO 值有 -50 到 +50，比例字体下 "-28" 与 "0"
            // 的宽度差近一倍，八列并排时数字会左右乱跳，读不出一列对齐的读数。
            FontFamily? monoFont = TryFindResource("MonoFontFamily") as FontFamily;

            const int Columns = 8;   // 必须与 XAML 里 UniformGrid.Columns 一致

            for (int core = 0; core < totalCores; core++)
            {
                int col = core % Columns;
                int row = core / Columns;
                bool lastCol = col == Columns - 1;
                bool lastRow = core + Columns >= totalCores;

                TextBox box = new()
                {
                    Tag = core,
                    IsEnabled = false,
                    Style = coreBoxStyle,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, lastCol ? 0 : 6, lastRow ? 0 : 8),
                    ToolTip = $"核心 {core}（CCD{core / coresPerCcd}）"
                };

                if (monoFont != null)
                    box.FontFamily = monoFont;

                // 字号与字重：全局 TextBox 是 13 / Normal，CO 数值要按 L3 处理。
                // 局部覆盖是安全的 —— 模板用的是 TemplateBinding，会读到这里的值。
                box.FontSize = 16;
                box.FontWeight = FontWeights.Bold;

                CoreBoxes.Add(box);
                CoreGrid.Children.Add(box);
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

            (SmuSnapshot? snapshot, string? fatalError) = await ReadSnapshotAsync();

            if (!TryUseSnapshot(snapshot, fatalError, "SMU 只读探测失败", "读取失败", "SMU 探测失败"))
                return;

            ApplySnapshot(snapshot!);
        }

        /// <summary>后台读一次快照。失败时返回 null，reason 说明原因。</summary>
        private static async System.Threading.Tasks.Task<(SmuSnapshot? Snapshot, string? Error)> ReadSnapshotAsync()
        {
            try
            {
                SmuSnapshot snapshot = await System.Threading.Tasks.Task.Run(() =>
                {
                    SmuReader reader = SmuReader.Shared;
                    if (!reader.IsAvailable)
                        throw new InvalidOperationException(reader.LastError ?? "ZenStates-Core 初始化失败");
                    return reader.ReadSnapshot();
                });

                return (snapshot, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// 判断一次探测结果能不能用。失败时统一记日志、置状态、弹提示，并返回 false。
        /// </summary>
        private bool TryUseSnapshot(
            SmuSnapshot? snapshot, string? fallbackError, string logPrefix, string dialogLead, string dialogTitle)
        {
            if (snapshot is { Success: true })
                return true;

            string reason = snapshot?.Error ?? fallbackError ?? "未知原因";
            LogService.Error($"{logPrefix}：{reason}");
            SetSmuState(SmuUiState.Failed, reason);

            // 【为什么从弹窗改成 Toast】
            // 这是"第一层失败提示"，用户在切页的瞬间就会看到，没有需要他现场做的决定
            // （要么装驱动重启，要么本来就不是 AMD 平台）。弹窗只会逼他先关掉它，
            // 才能去看日志。完整的排查指引仍然写进 ToolTip 与日志，需要的人随时能看。
            ToastService.Error($"{dialogLead}：{reason}。请确认已安装 PawnIO 驱动并以管理员身份运行");
            return false;
        }

        /// <summary>
        /// 把 SMU 返回的 cpuName 拆成「型号」与「规格」两段。
        ///
        /// 输入形如：
        ///   AMD Ryzen 9 9955HX 16-Core Processor
        ///   AMD Ryzen 7 7735HS with Radeon Graphics
        ///   AMD EPYC 7763 64-Core Processor
        /// 输出：
        ///   model = "AMD Ryzen 9 9955HX" / spec = "16-Core Processor"
        ///   model = "AMD Ryzen 7 7735HS" / spec = "with Radeon Graphics"
        ///
        /// 拆分点是「第一个数字开头、且后面以 -Core / -Thread 开头的词」。
        /// 不能简单地按第一个数字切：型号本身带数字（9955HX、7735HS），
        /// 也不能按空格数切： "Ryzen 9" 和 "Ryzen" 两代命名都不一样。
        /// 匹配不到就整串当型号、规格留空 —— 宁可少显示半行，不要显示错位的半行。
        /// </summary>
        private static void SplitCpuName(string raw, out string model, out string spec)
        {
            model = raw.Trim();
            spec = string.Empty;

            if (string.IsNullOrEmpty(model))
                return;

            // 从右往左找最后一个「N-Core」/「N-Thread」/「with xxx」片段：
            // 规格总在尾部，型号在前，从右找不会误切到 "Ryzen 9" 里的 9。
            Match match = Regex.Match(
                model,
                @"^(?<model>.+?)\s+(?<spec>(?:\d+-(?:Core|Thread)\b.*|with\b.*))$",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                model = match.Groups["model"].Value.Trim();
                spec = match.Groups["spec"].Value.Trim();
            }
        }

        /// <summary>
        /// 副行文案：规格 · SMU 版本。
        /// 规格拿不到时只显示 SMU；两者都没有时给一句「处理器已识别」，
        /// 避免副行整行空白，让卡片看起来像没填完。
        /// </summary>
        private static string ComposeSubLine(string spec, string smuVersion)
        {
            string smu = string.IsNullOrWhiteSpace(smuVersion) ? string.Empty : $"SMU {smuVersion}";

            if (!string.IsNullOrWhiteSpace(spec) && smu.Length > 0)
                return $"{spec} · {smu}";

            if (!string.IsNullOrWhiteSpace(spec))
                return spec;

            if (smu.Length > 0)
                return smu;

            return "处理器已识别";
        }

        private void ApplySnapshot(SmuSnapshot snapshot)
        {
            // 页头两行：主行只放型号，副行放「核心规格 · SMU 版本」。
            // SMU 返回的 cpuName 是 "AMD Ryzen 9 9955HX 16-Core Processor"，
            // 型号和规格混在一行，18px 下会顶到右边的按钮组；拆开后各归各位。
            if (!string.IsNullOrWhiteSpace(snapshot.CpuName))
            {
                SplitCpuName(snapshot.CpuName, out string model, out string spec);

                CpuNameText.Text = model;
                CpuSubText.Text = ComposeSubLine(spec, snapshot.SmuVersion);
            }

            SetSmuState(SmuUiState.Ready, $"SMU {snapshot.SmuVersion} · {snapshot.SmuType}");

            int coresPerCcd = (int)Math.Max(1, snapshot.CoresPerCcd);
            int totalCores = snapshot.CoreMargins.Length;
            // 核心数变化时才重建网格（换 CPU / 驱动升级自愈）；否则保留用户已填的值。
            if (totalCores > 0 && CoreBoxes.Count != totalCores)
                BuildCoreGrid(totalCores, coresPerCcd);

            // 网格提示（v12 新增的文字位）给「输入范围 + 当前读取情况」，
            // 只读描述，不参与任何写入逻辑。
            //
            // 【为什么副行不在这里写拓扑】
            // 用户要的副行格式是「16-Core Processor · SMU xxx」—— 核心数由 CPU 型号
            // 后缀直接给出，再补一句「16 核 · 2 个 CCD · 每 CCD 8 核」是同一件事说两遍，
            // 而且两处数字一旦不一致（比如 cpuName 里写 16 核、拓扑读到 8 核）
            // 用户会不知道该信哪个。拓扑明细留给 CoreGridHint 和 SMU 自检报告。
            if (totalCores > 0)
            {
                CoreGridHint.Text = $"共 {totalCores} 个核心，每格对应一个物理核心；负值降压、正值加压，留“--”表示不写入。";
            }
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

        // ---------- 电源计划策略（Windows 电源策略层） ----------

        /// <summary>
        /// 读取当前电源方案的四个索引并反推出最接近的档位，回填下拉框。
        ///
        /// 读不到就置灰并给出原因 —— 不做「假装能改」的开关。
        /// 读得到但对不上任何一档时，选中项留空并显示「自定义」，
        /// 同时把说明行换成实际数值：用户需要知道「现在到底是什么状态」，
        /// 而不是被强行归到某个档里。
        /// </summary>
        private void LoadPowerPlanPolicy()
        {
            if (!ProcessorPowerPolicy.TryDetect(out PowerPlanProfile? profile, out string error))
            {
                SetPlanUnavailable(error);
                return;
            }

            _planCurrent = profile;

            if (profile == null)
            {
                _suppressPlanEvent = true;
                try
                {
                    PlanCombo.SelectedIndex = -1;
                    PlanCombo.Text = "自定义";
                }
                finally
                {
                    _suppressPlanEvent = false;
                }

                PlanHint.Text = "当前电源方案的设置不属于任何预设档位（可能被系统或其他软件调过）。选中任意档位即可覆盖。";
            }
            else
            {
                ApplyPlanToUi(profile.Value);
                PlanHint.Text = ProcessorPowerPolicy.Description(profile.Value);
            }

            PlanCombo.IsEnabled = true;
        }

        /// <summary>把档位同步到下拉框，期间屏蔽 SelectionChanged。</summary>
        private void ApplyPlanToUi(PowerPlanProfile profile)
        {
            _suppressPlanEvent = true;
            try
            {
                PlanCombo.SelectedIndex = profile switch
                {
                    PowerPlanProfile.Gentle => 1,
                    PowerPlanProfile.Balanced => 2,
                    PowerPlanProfile.Quiet => 3,
                    _ => 0, // 解除限制
                };
            }
            finally
            {
                _suppressPlanEvent = false;
            }
        }

        /// <summary>
        /// 用户切换档位。
        ///
        /// 写入失败必须回滚下拉框到实际值：不滚的话界面显示「静音」而系统
        /// 实际仍在全速跑，用户下一次回来会看到一个自己没设过的状态。
        /// </summary>
        private async void PlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPlanEvent || !IsLoaded)
                return;

            PowerPlanProfile target = PlanCombo.SelectedIndex switch
            {
                1 => PowerPlanProfile.Gentle,
                2 => PowerPlanProfile.Balanced,
                3 => PowerPlanProfile.Quiet,
                _ => PowerPlanProfile.Unrestricted,
            };

            if (_planCurrent == target)
                return;

            string label = ProcessorPowerPolicy.Label(target);
            string previousHint = PlanHint.Text;

            // 写入是四次 powrprof 调用 + 一次重新激活，在低端机上实测 80~150ms。
            // 同步执行会让下拉框「卡在半开状态」一下，放后台线程，期间锁住控件。
            PlanCombo.IsEnabled = false;
            PlanHint.Text = $"正在应用「{label}」…";

            PowerPlanResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(() => ProcessorPowerPolicy.Apply(target));
            }
            catch (Exception ex)
            {
                result = PowerPlanResult.Fail("电源策略", ex.Message);
            }

            PlanCombo.IsEnabled = true;

            if (!result.Ok)
            {
                LogService.Warn($"电源计划策略写入失败（{result.Setting}，目标 {label}）：{result.Error}");

                // 回滚 UI 到系统真实值
                if (_planCurrent is PowerPlanProfile actual)
                    ApplyPlanToUi(actual);
                else
                    PlanCombo.SelectedIndex = -1;

                PlanHint.Text = previousHint;

                ErrorNotifyService.Report(
                    $"无法应用「{label}」：{result.Error}",
                    "电源计划策略",
                    $"无法修改「{result.Setting}」",
                    result.Error);
                return;
            }

            _planCurrent = target;
            PlanHint.Text = ProcessorPowerPolicy.Description(target);
            LogService.Info($"电源计划策略：已切换到「{label}」");

            // 电池供电那一路若写失败，只提示一次，不影响用户已经看到的成功结果。
            // 这类情况多见于台式机（没有 DC 子组）或 OEM 锁死电池策略的方案。
            if (ProcessorPowerPolicy.LastDcWarning is string dcWarning)
            {
                LogService.Warn($"电源计划策略：{dcWarning}");
                ToastService.Info($"已切换到「{label}」（电池供电设置未能写入，插电状态已生效）");
                return;
            }

            ToastService.Success($"电源计划策略：{label}");
        }

        /// <summary>不可用时的统一降级：禁用控件 + 在说明行写清楚原因。</summary>
        private void SetPlanUnavailable(string reason)
        {
            _suppressPlanEvent = true;
            try
            {
                PlanCombo.SelectedIndex = -1;
                PlanCombo.Text = string.Empty;
            }
            finally
            {
                _suppressPlanEvent = false;
            }

            PlanCombo.IsEnabled = false;
            PlanHint.Text = $"当前系统无法读取电源策略：{reason}";
            LogService.Warn($"电源计划策略不可用：{reason}");
        }

        // ---------- 升频模式 ----------

        /// <summary>回填升频模式时屏蔽 SelectionChanged（同 PlanCombo 的理由）。</summary>
        private bool _suppressBoostEvent;

        /// <summary>当前实际升频模式；null = 系统值不在预设表里。</summary>
        private ProcessorPowerTuning.BoostMode? _boostCurrent;

        /// <summary>
        /// 读升频模式并回填下拉框。
        ///
        /// 读不到就禁用并写清原因（可能是虚拟机、或被 OEM 精简掉的方案），
        /// 不显示一个「看起来能点但点了什么都没发生」的下拉框。
        /// </summary>
        private void LoadBoostMode()
        {
            if (!ProcessorPowerTuning.TryReadBoost(out uint raw, out string error))
            {
                SetBoostUnavailable(error);
                return;
            }

            if (raw == uint.MaxValue)
            {
                // 索引存在但没设定值：这是合法状态，Windows 会按方案默认走。
                _boostCurrent = null;
                SetBoostSelection(-1, "系统默认");
                BoostHint.Text = "当前方案未单独设定升频模式，沿用系统默认行为。选中任一模式即可覆盖。";
                BoostCombo.IsEnabled = true;
                return;
            }

            _boostCurrent = FindBoostMode(raw);
            if (_boostCurrent == null)
            {
                SetBoostSelection(-1, $"未知（{raw}）");
                BoostHint.Text = $"系统返回了预设之外的升频值（{raw}），可能由其他软件写入。选中任一模式即可覆盖。";
                BoostCombo.IsEnabled = true;
                return;
            }

            ApplyBoostToUi(_boostCurrent.Value);
            BoostHint.Text = BoostHintFor(_boostCurrent.Value);
            BoostCombo.IsEnabled = true;
        }

        /// <summary>数值 → 枚举。位掩码值必须精确匹配，不做模糊归类。</summary>
        private static ProcessorPowerTuning.BoostMode? FindBoostMode(uint raw)
        {
            foreach ((ProcessorPowerTuning.BoostMode mode, _, _) in ProcessorPowerTuning.BoostOptions)
            {
                if ((uint)mode == raw)
                    return mode;
            }

            return null;
        }

        /// <summary>枚举 → 下拉框序号。顺序必须与 XAML 里的 ComboBoxItem 一致。</summary>
        private static int BoostIndex(ProcessorPowerTuning.BoostMode mode) => mode switch
        {
            ProcessorPowerTuning.BoostMode.AggressiveEfficient => 0,
            ProcessorPowerTuning.BoostMode.Aggressive => 1,
            ProcessorPowerTuning.BoostMode.Enabled => 2,
            ProcessorPowerTuning.BoostMode.Efficient => 3,
            ProcessorPowerTuning.BoostMode.Disabled => 4,
            _ => -1,
        };

        private static string BoostHintFor(ProcessorPowerTuning.BoostMode mode)
        {
            foreach ((ProcessorPowerTuning.BoostMode item, _, string hint) in ProcessorPowerTuning.BoostOptions)
            {
                if (item == mode)
                    return hint;
            }

            return string.Empty;
        }

        private void ApplyBoostToUi(ProcessorPowerTuning.BoostMode mode)
        {
            SetBoostSelection(BoostIndex(mode), null);
        }

        /// <summary>改选中项并屏蔽事件。<paramref name="displayText"/> 非空时用它覆盖显示文本（自定义态）。</summary>
        private void SetBoostSelection(int index, string? displayText)
        {
            _suppressBoostEvent = true;
            try
            {
                BoostCombo.SelectedIndex = index;
                if (displayText != null)
                    BoostCombo.Text = displayText;
            }
            finally
            {
                _suppressBoostEvent = false;
            }
        }

        private void SetBoostUnavailable(string reason)
        {
            SetBoostSelection(-1, string.Empty);
            BoostCombo.IsEnabled = false;
            BoostHint.Text = $"当前系统无法读取升频模式：{reason}";
            LogService.Warn($"升频模式不可用：{reason}");
        }

        private async void BoostCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBoostEvent || !IsLoaded)
                return;

            ProcessorPowerTuning.BoostMode? target = BoostCombo.SelectedIndex switch
            {
                0 => ProcessorPowerTuning.BoostMode.AggressiveEfficient,
                1 => ProcessorPowerTuning.BoostMode.Aggressive,
                2 => ProcessorPowerTuning.BoostMode.Enabled,
                3 => ProcessorPowerTuning.BoostMode.Efficient,
                4 => ProcessorPowerTuning.BoostMode.Disabled,
                _ => null,
            };

            if (target == null || _boostCurrent == target)
                return;

            string label = ProcessorPowerTuning.BoostLabel((uint)target.Value);
            string previousHint = BoostHint.Text;

            BoostCombo.IsEnabled = false;
            BoostHint.Text = $"正在应用「{label}」…";

            PowerPlanResult result;
            try
            {
                ProcessorPowerTuning.BoostMode mode = target.Value;
                result = await System.Threading.Tasks.Task.Run(() => ProcessorPowerTuning.WriteBoost(mode));
            }
            catch (Exception ex)
            {
                result = PowerPlanResult.Fail("升频模式", ex.Message);
            }

            BoostCombo.IsEnabled = true;

            if (!result.Ok)
            {
                LogService.Warn($"升频模式写入失败（{result.Setting}，目标 {label}）：{result.Error}");

                // 回滚 UI 到系统真实值，否则界面显示的值与硬件不一致
                if (_boostCurrent is ProcessorPowerTuning.BoostMode actual)
                    ApplyBoostToUi(actual);
                else
                    BoostCombo.SelectedIndex = -1;

                BoostHint.Text = previousHint;
                ToastService.Error($"无法应用升频模式「{label}」：{result.Error}");
                return;
            }

            _boostCurrent = target.Value;
            BoostHint.Text = BoostHintFor(target.Value);
            LogService.Info($"升频模式已切换为「{label}」");
            ToastService.Success($"升频模式：{label}");
        }

        // ---------- 核心停放 ----------

        /// <summary>回填滑杆时屏蔽 ValueChanged 的闸门。</summary>
        private bool _suppressParkingEvent;

        /// <summary>当前生效的停放上限（读失败为 null，此时控件禁用）。</summary>
        private uint? _parkingCurrent;

        /// <summary>
        /// 读核心停放上限并回填滑杆与输入框。
        ///
        /// 用注册表读而不是 PowerReadACValueIndex，理由见
        /// <see cref="ProcessorPowerTuning.TryReadCoreParking"/>：
        /// 范围型索引的「用户设定值」只在注册表里，API 返回的可能是被系统临时调整后的实时值。
        /// </summary>
        private void LoadCoreParking()
        {
            if (!ProcessorPowerTuning.TryReadCoreParking(out uint percent, out string error))
            {
                _parkingCurrent = null;

                // 【读不到就把控件整块收起来，只留一句原因】
                // 旧版把滑杆和输入框灰着摆在那儿 —— 用户看到控件就一定会去拖，
                // 拖完发现数字弹回去，而唯一的解释挤在另一行灰字里。
                // 既然这项在本机根本不可写，它就不该以「一个控件」的形态存在。
                ParkingEditablePanel.Visibility = Visibility.Collapsed;
                ParkingUnavailablePanel.Visibility = Visibility.Visible;
                ParkingUnavailableText.Text = DescribeParkingUnavailable(error);
                return;
            }

            ParkingEditablePanel.Visibility = Visibility.Visible;
            ParkingUnavailablePanel.Visibility = Visibility.Collapsed;

            _parkingCurrent = percent;

            _suppressParkingEvent = true;
            try
            {
                ParkingSlider.Value = percent;
                ParkingBox.Text = percent.ToString();
            }
            finally
            {
                _suppressParkingEvent = false;
            }

            ParkingSlider.IsEnabled = true;
            ParkingBox.IsEnabled = true;
            ParkingHint.Text = ProcessorPowerTuning.ParkingHint(percent);
        }

        /// <summary>
        /// 核心停放不可用时的那一句提示。
        ///
        /// 【为什么不再按错误串分流成三长句】
        /// 旧实现把 powrprof 的原始错误贴进正文，于是界面上出现了：
        ///
        ///     当前系统无法读取核心停放设置（当前电源方案不提供核心停放设置。），此项已停用。
        ///
        /// 括号里那半句本身就是「为什么」，把它原样嵌进「无法读取（…）」的括号里，
        /// 一句话里说了两遍同一件事，还带着句号和全角括号 —— 读起来像把异常对象
        /// 直接 ToString 贴到了界面上。
        ///
        /// 用户在这个位置需要知道的只有一件事：**这一项现在用不了，别找了**。
        /// 至于具体是「方案没暴露」还是「注册表没有这一项」，都是同一件事的两种技术说法，
        /// 对用户没有区别。所以统一成一句 12 个字的话；原始错误仍然进日志，
        /// 排查时看得到，界面上不占地方。
        /// </summary>
        private static string DescribeParkingUnavailable(string error)
        {
            // 原始错误只进日志，不进界面。放这里是为了保证「读不到」与「写不进去」
            // 两条路径都能留下证据 —— 之前同一句话被拼进 UI，日志里反而只有半句。
            LogService.Warn($"核心停放不可用的原始原因：{error}");
            return ParkingUnsupportedText;
        }

        /// <summary>不可用时展示的唯一一句话。</summary>
        private const string ParkingUnsupportedText = "当前电源方案不支持核心停放，此项已停用";

        /// <summary>
        /// 拖滑杆：只同步输入框与文案，不写系统。
        ///
        /// 【为什么不边拖边写】拖动一次会产生几十次 ValueChanged，
        /// 每次都写注册表 + 重新激活电源方案，等于把 powrprof 当成动画帧回调，
        /// 界面会明显掉帧，系统电源服务也被反复打扰。
        /// 落盘的时机是「松手」——由 <see cref="ParkingSlider_PreviewMouseUp"/> 触发；
        /// 键盘微调（方向键）则由 Thumb 之外的 KeyUp 走同一路径。
        /// </summary>
        private void ParkingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressParkingEvent)
                return;

            uint value = (uint)Math.Round(e.NewValue);

            _suppressParkingEvent = true;
            try
            {
                ParkingBox.Text = value.ToString();
            }
            finally
            {
                _suppressParkingEvent = false;
            }

            ParkingHint.Text = ProcessorPowerTuning.ParkingHint(value) + "（松开鼠标后生效）";
        }

        /// <summary>松手才提交滑杆的值。</summary>
        private void ParkingSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _ = CommitParkingAsync((uint)Math.Round(ParkingSlider.Value));
        }

        /// <summary>输入框回车：提交并同步滑杆。</summary>
        private void ParkingBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;

            e.Handled = true;
            CommitFromBox();
        }

        /// <summary>输入框失焦：值合法就提交，不合法就还原成当前值。</summary>
        private void ParkingBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitFromBox();
        }

        private void CommitFromBox()
        {
            if (!uint.TryParse(ParkingBox.Text, out uint value) || value > 100)
            {
                // 非法输入不弹窗：直接把框还原成系统当前值，
                // 用户会立刻看到「输入没被接受」，比弹一个提示再关掉更快。
                ParkingBox.Text = (_parkingCurrent ?? 0).ToString();
                if (_parkingCurrent is uint current)
                    ParkingHint.Text = ProcessorPowerTuning.ParkingHint(current);
                return;
            }

            // 滑杆跟着框走，保持两者一致（框允许输入 5 的倍数以外的值）
            _suppressParkingEvent = true;
            try
            {
                ParkingSlider.Value = value;
            }
            finally
            {
                _suppressParkingEvent = false;
            }

            _ = CommitParkingAsync(value);
        }

        /// <summary>
        /// 把停放上限写到系统。
        ///
        /// 写入是「改注册表 + 重新激活方案」，几十毫秒量级，放后台线程；
        /// 期间禁用两个控件，避免连续拖动堆起多个并发写入（后一个的结果会覆盖前一个的提示）。
        /// </summary>
        private async System.Threading.Tasks.Task CommitParkingAsync(uint value)
        {
            if (_parkingCurrent == value)
                return;

            uint previous = _parkingCurrent ?? value;

            ParkingSlider.IsEnabled = false;
            ParkingBox.IsEnabled = false;
            ParkingHint.Text = $"正在应用 {value}%…";

            PowerPlanResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(() => ProcessorPowerTuning.WriteCoreParking(value));
            }
            catch (Exception ex)
            {
                result = PowerPlanResult.Fail("核心停放", ex.Message);
            }

            ParkingSlider.IsEnabled = true;
            ParkingBox.IsEnabled = true;

            if (!result.Ok)
            {
                LogService.Warn($"核心停放写入失败（目标 {value}%）：{result.Error}");

                // 回滚到写入前的值
                _suppressParkingEvent = true;
                try
                {
                    ParkingSlider.Value = previous;
                    ParkingBox.Text = previous.ToString();
                }
                finally
                {
                    _suppressParkingEvent = false;
                }

                ParkingHint.Text = ProcessorPowerTuning.ParkingHint(previous);
                ToastService.Error($"无法设置核心停放：{result.Error}");
                return;
            }

            _parkingCurrent = value;
            ParkingHint.Text = ProcessorPowerTuning.ParkingHint(value);
            LogService.Info($"核心停放上限已设为 {value}%");
            ToastService.Success($"核心停放：{value}%");
        }

        // ---------- 控件交互 ----------

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
        /// 读取并校验 FMax 输入；失败返回 null（已提示）。
        /// 越界时不再拦截，而是自动收敛到边界值（信息提示，非警告拦截）后继续。
        /// </summary>
        private uint? CollectFMax()
        {
            if (!uint.TryParse(FMaxBox.Text, out uint fmax))
            {
                // 输入非法 = 用户刚打完字、焦点还在框里。用 Toast 提示而不是弹窗：
                // 弹窗会抢走焦点，用户连"我刚才输的是什么"都看不到就得先关掉它。
                ToastService.Warning($"FMax「{FMaxBox.Text}」不是有效的频率值");
                LogService.Warn($"FMax 输入非法：\"{FMaxBox.Text}\"");
                FMaxBox.Focus();
                FMaxBox.SelectAll();
                return null;
            }

            if (fmax > FMaxMax)
            {
                fmax = FMaxMax;
                FMaxBox.Text = fmax.ToString();
                ToastService.Info($"FMax 已自动调整为 {FMaxMax} MHz（超出上限）");
            }
            else if (fmax < FMaxMin)
            {
                fmax = FMaxMin;
                FMaxBox.Text = fmax.ToString();
                ToastService.Info($"FMax 已自动调整为 {FMaxMin} MHz（低于下限）");
            }

            return fmax;
        }

        // ---------- 唯一的写入口：顶部「应用」——CO + FMax 一起写 ----------

        private void ApplyAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !TryCollectInputs(out int[]? margins, out uint fmax))
                return;

            // 【为什么不再弹确认框】
            // 这个弹窗原本是「写 SMU 前的最后一道闸」，但它拦不住任何真实错误：
            // 点「确定」和点「应用全部」之间没有任何额外信息，用户读完那句话
            // 仍然只能选「确定」。真正的保护在下游 —— 写入失败会回滚并报错，
            // 黑屏也能重启恢复。留一个纯仪式感的确认框，只是每次都要多点一下。
            // 改成一条 Toast 把「即将写什么」告诉用户，风险句挪到按钮的
            // ToolTip 与提示文案里，需要看的人随时能看到。
            int activeCores = margins!.Count(v => v != int.MinValue);

            LogService.Info($"开始下发：CO {activeCores} 个核心，FMax {fmax} MHz");
            ToastService.Info($"正在写入 CO（{activeCores} 核）与 FMax {fmax} MHz…");

            _ = ApplyAllAsync(margins!, fmax);
        }

        /// <summary>收集 CO 与 FMax 两组输入；任一不合法就提示并返回 false。</summary>
        private bool TryCollectInputs(out int[]? margins, out uint fmax)
        {
            fmax = 0;
            margins = CollectCoreMargins(out string? coReject);
            if (coReject != null)
            {
                // 逐条列出是哪几个核心错了。Toast 承载不了多行，所以这里保留弹窗 ——
                // 但只在「确实有多个字段要用户逐个改」时才用，不是所有失败都弹。
                DarkDialog.ShowWarning(coReject, "CO 输入非法");
                return false;
            }

            uint? collected = CollectFMax();
            if (collected == null)
                return false;

            fmax = collected.Value;
            return true;
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
                SetSmuState(SmuUiState.Failed, ex.Message);
                ToastService.Error($"写入过程中发生异常：{ex.Message}（详见日志）");
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
                // 普通反馈，改 Toast：用户刚点完「应用全部」，眼睛还在按钮上，
                // 弹一个必须点确定的框纯属打断。回读结果写进提示正文，
                // 用户扫一眼就知道 CO 核数与 FMax 都对上了。
                ToastService.Success(
                    $"已应用：CO {profile.CurveOptimizer.Length} 核 / FMax {fmax} MHz，回读一致");

                _ = RefreshAfterWriteAsync();
            }
            else
            {
                SetSmuState(SmuUiState.Failed, "写入存在失败");

                // 【部分失败：用一条摘要 Toast + 完整明细进日志】
                // 这里原本是弹窗，正文是多行清单（哪个核心被拒、回读不一致）。
                // 但用户此刻要做的决定只有一个 —— "要不要重试"，
                // 而这个决定不需要读完 16 个核心的明细才能做。
                // 明细一字不少地写进日志，需要的人点开"日志"就能看到。
                System.Text.StringBuilder detail = new();
                if (!coOk)
                {
                    if (!string.IsNullOrEmpty(co.Fatal)) detail.AppendLine($"· {co.Fatal}");
                    if (co.WriteFailed.Count > 0) detail.AppendLine($"· CO 被拒核心：{string.Join(", ", co.WriteFailed)}");
                    if (co.VerifyFailed.Count > 0) detail.AppendLine($"· CO 回读不一致核心：{string.Join(", ", co.VerifyFailed)}");
                }
                if (!fmOk)
                {
                    detail.AppendLine(fm.Fatal != null
                        ? $"· FMax：{fm.Fatal}"
                        : $"· FMax：回读不一致（请求 {fm.Requested}，回读 {fm.ReadBack}）");
                }

                LogService.Error("部分写入失败，未保存配置：\n" + detail);

                string summary = !coOk && !fmOk ? "CO 与 FMax 均未完全生效"
                    : !coOk ? "部分核心的 CO 未生效"
                    : "FMax 未生效";

                ToastService.Error($"{summary}（已写入的部分仍生效，配置未落盘；明细见日志）");
            }
        }

        private async System.Threading.Tasks.Task RefreshAfterWriteAsync()
        {
            try
            {
                (SmuSnapshot? snapshot, _) = await ReadSnapshotAsync();
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
            if (!TryCollectInputs(out int[]? margins, out uint fmax))
                return;

            TuningProfile profile = TuningStore.Profile;
            profile.CurveOptimizer = margins!.Where(v => v != int.MinValue).ToArray();
            profile.FMaxMHz = fmax;

            TuningStore.Save(profile);

            LogService.Info("保存当前配置到本地（仅落盘，未写 SMU）");
            // 普通反馈 → Toast。注意文案里的「未写入 SMU」必须保留：
            // 这是「保存」和「应用」唯一的行为区别，用户最容易混淆的一点。
            ToastService.Success("已保存到本地配置（未写入 SMU）");
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
            ToastService.Info("已加载本地配置并回填（未写入 SMU）");
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

            SmuSnapshot? snapshot;
            string? error;
            try
            {
                (snapshot, error) = await ReadSnapshotAsync();
            }
            finally
            {
                _busy = false;
                SetWriteButtonsEnabled(true);
            }

            if (!TryUseSnapshot(snapshot, error, "手动刷新失败", "刷新失败", "刷新失败"))
                return;

            ApplySnapshot(snapshot!);
            LogService.Info("已按 SMU 现值刷新界面（未写入 SMU、未改本地配置）");
        }
    }
}
