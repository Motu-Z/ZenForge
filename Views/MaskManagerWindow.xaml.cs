using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 自定义掩码管理窗口：左侧列表 + 右侧核心勾选。
    /// 新增 / 编辑 / 删除均落到 <see cref="MaskStore"/>（masks.json），
    /// 保存后通过 Changed 事件联动主界面下拉框。
    /// </summary>
    public partial class MaskManagerWindow : Window
    {
        private int _physicalCores;
        private int _threadsPerCore;
        private int _logicalCount;
        private readonly List<CheckBox> _allBoxes = new();

        /// <summary>每个 CCD 一块的折行容器，用于按可用宽度重算列宽。</summary>
        private readonly List<WrapPanel> _corePanels = new();

        /// <summary>单个勾选框的自然宽度（含间距），由字体实测得出。</summary>
        private double _itemNaturalWidth = 120;

        /// <summary>正在编辑的掩码 Id（null = 新建）。</summary>
        private string? _editingId;

        /// <summary>
        /// 当前编辑中的名称。
        /// 名称输入框已从右栏移到列表行内（就地改名），所以不再从控件读文本，
        /// 而是由行内编辑把结果写到这个字段。
        /// </summary>
        private string _pendingName = string.Empty;

        public MaskManagerWindow()
        {
            InitializeComponent();
            MaskStore.Changed += OnMasksChanged;
            // MaskStore 是静态存储，窗口关闭后必须解绑，否则反复开关窗口会累积回调，
            // 并让已关闭的窗口无法被回收。
            Closed += (_, _) => MaskStore.Changed -= OnMasksChanged;

            // 关窗时只要收起改名输入框即可。
            // 改名只在按回车时落盘，所以这里不需要（也不应该）提交任何东西。
            Closing += (_, _) => CancelRowRename();
            Loaded += (_, _) => BuildUI();
        }

        private void BuildUI()
        {
            _logicalCount = Math.Max(1, CpuSetBinder.LogicalProcessorCount);

            if (_logicalCount <= 1)
            {
                ShowTopologyError();
                RefreshList();
                return;
            }

            _threadsPerCore = _logicalCount % 2 == 0 ? 2 : 1;
            _physicalCores = Math.Max(1, _logicalCount / _threadsPerCore);

            BuildBoxes();
            RefreshList();

            // 首帧布局完成后才能拿到真实可用宽度，这里补一次列宽计算
            Dispatcher.BeginInvoke(new Action(ReflowCorePanels),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CoreScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 宽度变化才需要重排列宽；高度变化（滚轮/内容变多）不用管
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 0.5)
                ReflowCorePanels();
        }

        private void ShowTopologyError()
        {
            _physicalCores = 0;
            _threadsPerCore = 0;
            CorePanel.Children.Add(new TextBlock
            {
                Text = "未能从系统读取逻辑处理器信息，勾选列表不可用。\n" +
                       "该读取需要管理员权限，重新打开本窗口后可重试。",
                Foreground = (Brush)Application.Current.Resources["State.Danger"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        /// <summary>
        /// 按 CCD 分段生成核心 / 线程勾选框。
        ///
        /// 【布局：每核一枚「Core N T0/T1」勾选框，按核心顺序排，自动折行】
        ///   列数不写死，由可用宽度算出（见 <see cref="ReflowCorePanels"/>）。
        ///   写死列数两头都难看：窗口一宽右侧留一条空白，窗口一窄又得横向滚动。
        ///
        ///   先按最长标签（两位数核号 + T1）量出单个勾选框的自然宽度，
        ///   再用它算出「能放几列」，最后把可用宽度均分给这几列 ——
        ///   这样每列间距一致、右侧不留缝，列线在各 CCD 块之间也对得齐。
        /// </summary>
        private void BuildBoxes()
        {
            int ccdCount = Math.Max(1, CpuSetBinder.CcdCount);
            int coresPerCcd = Math.Max(1, _physicalCores / ccdCount);

            // 先量出单个勾选框的自然宽度，实际列宽由可用宽度均分（见 ReflowCorePanels）。
            _itemNaturalWidth = MeasureCheckBoxWidth();

            for (int ccd = 0; ccd < ccdCount; ccd++)
            {
                int lo = ccd * coresPerCcd;
                // 最后一个 CCD 吃掉余数，避免除不尽时漏核
                int hi = ccd == ccdCount - 1 ? _physicalCores - 1 : lo + coresPerCcd - 1;
                if (lo > hi) break;

                AddHeader(CorePanel, $"CCD{ccd}  ·  Core {lo} - {hi}");

                var panel = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    // 底部留 4：CCD 标题已经带了 14 的上边距，这里再大就散架
                    Margin = new Thickness(0, 0, 0, 4)
                };

                for (int core = lo; core <= hi; core++)
                {
                    for (int t = 0; t < _threadsPerCore; t++)
                    {
                        int logical = core + t * _physicalCores;
                        panel.Children.Add(MakeBox(core, t, logical));
                    }
                }

                _corePanels.Add(panel);
                CorePanel.Children.Add(panel);
            }
        }

        /// <summary>
        /// 量一枚勾选框需要多宽。
        ///
        /// 用真实控件量而不是拍脑袋写数字：字体、DPI 缩放、系统语言都会影响宽度，
        /// 写死会在 125% / 150% 缩放下把「Core 11 T1」挤成两行。
        /// 取本机最大物理核号拼出最长标签（如「Core 191 T1」）来量，保证够用。
        /// </summary>
        private double MeasureCheckBoxWidth()
        {
            int maxCore = Math.Max(0, _physicalCores - 1);
            string probeText = $"Core {maxCore}  T{Math.Max(0, _threadsPerCore - 1)}";

            // 【为什么不用「建一个 CheckBox 再 Measure」】
            // 那个控件没有父级、不在可视树里，拿不到窗口的字体继承与 DPI 上下文：
            //    · FontFamily 回落成系统默认（本窗口指定的是 Microsoft YaHei UI）；
            //    · 在 150% 缩放的机器上量出来明显偏大。
            // 结果就是「能放几列」被算少 —— 4 列变 3 列，均分后每列被摊到 190px 宽，
            // 勾选框之间空出一大截（实测截图就是这样）。
            // 改用 FormattedText 直接按本窗口的字体、字号、DPI 算，不带模板噪声。
            var typeface = new Typeface(
                new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            var formatted = new FormattedText(
                probeText,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double textWidth = formatted.WidthIncludingTrailingWhitespace;

            // 勾选框方块 13 + 方框与文字的间距 7，再留 12 的列间呼吸位
            double natural = Math.Ceiling(textWidth) + 13 + 7 + 12;

            // 下限 106：字号被系统放大字号设置影响时，避免算出过窄的列
            return Math.Max(106, natural);
        }

        /// <summary>
        /// 按当前可用宽度把列宽均分，消除「最后一列右侧挂一条空白」。
        ///
        /// 之前 ItemWidth 写死成自然宽度，窗口一宽，一行能放 5 列却只剩 4 列宽的量，
        /// 右侧就多出一条谁也填不满的竖条（实测截图红框）。
        /// 改为：先用自然宽度算出「最多能放几列」，再把可用宽度**均分**给这几列。
        /// 列数会随窗口宽度变（窄 3 列、宽 5 列），但永远铺满、右侧不留缝。
        /// </summary>
        private void ReflowCorePanels()
        {
            if (_corePanels.Count == 0 || _itemNaturalWidth <= 0)
                return;

            double avail = CoreScroll.ViewportWidth;
            if (avail <= 0)
                avail = CoreScroll.ActualWidth - CoreScroll.Padding.Left - CoreScroll.Padding.Right;

            // 再扣掉可能出现的纵向滚动条，否则最后一列会被它压住
            if (CoreScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                avail -= SystemParameters.VerticalScrollBarWidth;

            if (avail <= _itemNaturalWidth / 2)
                return;

            // 列数上限 5：拉宽窗口时列数不再无限增长，否则 6 列以后每列空隙
            // 大得离谱、左右两半几乎断开。5 列是这张表还能读的上限。
            int cols = (int)Math.Floor(avail / _itemNaturalWidth);
            cols = Math.Max(1, Math.Min(cols, 5));

            double itemWidth = avail / cols;
            foreach (WrapPanel panel in _corePanels)
                panel.ItemWidth = itemWidth;
        }

        /// <summary>造一个「Core N  T0/T1」勾选框。Tag 存逻辑处理器序号（0 起的绝对编号）。</summary>
        private CheckBox MakeBox(int core, int thread, int logical)
        {
            var box = new CheckBox
            {
                Content = $"Core {core}  T{thread}",
                Tag = logical,
                IsChecked = false,
                // 固定高度：矩阵需要行高绝对一致，否则整块看起来是歪的。
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
                // 不写死 Foreground：之前用 Brushes.White，亮色主题下白底白字看不见。
                // 继承主题前景色即可。
                Focusable = true,
                FocusVisualStyle = null
            };

            _allBoxes.Add(box);
            return box;
        }

        private static void AddHeader(StackPanel host, string text)
        {
            host.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AccentBrush"],
                // 上边距 14：第一个 CCD 不需要额外留白（由外层布局给定），
                // 后续 CCD 靠它和上一块拉开距离。
                Margin = new Thickness(0, host.Children.Count == 0 ? 0 : 14, 0, 8)
            });
        }

        private void CollectMask(out ulong mask)
        {
            mask = 0;
            foreach (CheckBox box in _allBoxes)
            {
                if (box.IsChecked != true)
                    continue;

                int logical = (int)box.Tag;
                if (logical >= 0 && logical < 64)
                    mask |= 1UL << logical;
            }
        }

        private void ApplyMask(ulong mask)
        {
            foreach (CheckBox box in _allBoxes)
            {
                int logical = (int)box.Tag;
                box.IsChecked = logical < _logicalCount && (mask & (1UL << logical)) != 0;
            }
        }

        /// <summary>清空右侧勾选与编辑上下文（放弃草稿 / 删掉正在编辑的条目时用）。</summary>
        private void ResetEditor()
        {
            _editingId = null;
            _pendingName = string.Empty;
            ApplyMask(0);
        }

        // ---------- 列表 ----------

        /// <summary>
        /// 列表数据源。包含「已落盘的掩码」+「尚未保存的草稿」。
        ///
        /// 之所以自己维护一份集合而不是直接绑 MaskStore.Masks：
        /// 「新建」不能在用户点下去的一瞬间就写盘 —— 那等于替用户做决定，
        /// 点了新建又反悔会在配置里留下垃圾条目。草稿只活在这个集合里，
        /// 直到用户点「保存」才真正 Upsert。
        /// </summary>
        private readonly System.Collections.ObjectModel.ObservableCollection<CustomMask> _rows = new();

        /// <summary>当前未保存的草稿（null 表示没有）。</summary>
        private CustomMask? _draft;

        private bool _listReady;

        private void RefreshList()
        {
            if (!_listReady)
            {
                MaskList.ItemsSource = _rows;
                _listReady = true;
            }

            object? selected = MaskList.SelectedItem;

            _rows.Clear();
            foreach (CustomMask m in MaskStore.Masks)
                _rows.Add(m);

            // 草稿排在最后，且不与已落盘条目重复
            if (_draft != null && !MaskStore.Masks.Any(m => m.Id == _draft.Id))
                _rows.Add(_draft);

            if (selected is CustomMask prev)
            {
                CustomMask? again = _rows.FirstOrDefault(r => r.Id == prev.Id);
                if (again != null)
                    MaskList.SelectedItem = again;
            }

            UpdateCapacityUi();

            LogService.Debug(
                $"[掩码窗口] 列表刷新：已存 {MaskStore.Masks.Count} 条，草稿 {(_draft == null ? "无" : "有")}");
        }

        /// <summary>
        /// 刷新「N / 10」计数与新建按钮的可用状态。
        ///
        /// 计数把草稿也算进去 —— 草稿占了真实名额（保存时会被拒），
        /// 只显示已落盘条数会让人以为「还有空位，怎么点保存说满了」。
        /// </summary>
        private void UpdateCapacityUi()
        {
            int used = MaskStore.Masks.Count + (_draft == null ? 0 : 1);

            if (MaskCountText != null)
                MaskCountText.Text = $"{used} / {MaskStore.MaxCount}";

            if (NewButton == null)
                return;

            bool full = used >= MaskStore.MaxCount;
            NewButton.IsEnabled = !full;
            NewButton.ToolTip = full
                ? $"已达上限（最多 {MaskStore.MaxCount} 套）。删掉一条才能再新建。"
                : $"新建一条自定义掩码（最多 {MaskStore.MaxCount} 套）";
        }

        private void OnMasksChanged() => Dispatcher.Invoke(RefreshList);

        private void MaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 换选中行时放弃上一行的改名，不弹任何提示
            CancelRowRename();

            if (MaskList.SelectedItem is CustomMask mask)
                LoadIntoEditor(mask);
        }

        /// <summary>点击列表空白处 = 取消改名。</summary>
        private void MaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 命中的不是任何一行 → 点在了空白区
            DependencyObject? hit = e.OriginalSource as DependencyObject;
            ListBoxItem? item = hit == null ? null : ItemsControl.ContainerFromElement(MaskList, hit) as ListBoxItem;
            if (item == null)
                CancelRowRename();
        }

        /// <summary>把一条掩码填进右侧编辑区（选中列表项时调用）。</summary>
        private void LoadIntoEditor(CustomMask mask)
        {
            _editingId = mask.Id;
            _pendingName = mask.Name;
            ApplyMask(mask.Mask);
            LogService.Debug($"[掩码窗口] 载入编辑：{mask.Name}（{mask.Id}，mask=0x{mask.Mask:X}）");
        }

        // ==================== 行内改名 ====================

        /// <summary>正在改名的行 Id（null = 没在改名）。</summary>
        private string? _renamingId;

        /// <summary>改名输入框，供取消时收起。</summary>
        private TextBox? _renamingBox;

        /// <summary>裁剪文本时避免 TextChanged 递归。</summary>
        private bool _trimming;

        /// <summary>点击行内名称 → 原地变成输入框，全选原有文字。</summary>
        private void RowName_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CustomMask mask)
                return;

            MaskList.SelectedItem = mask;
            LoadIntoEditor(mask);
            BeginRowRename(fe, mask);

            // 标记已处理：否则 ListBox 会把这个 MouseUp 当成「切换选中」再处理一遍，
            // 触发 SelectionChanged → CancelRowRename，刚打开的输入框立刻被收起。
            e.Handled = true;
        }

        /// <summary>把某一行切换成编辑态并抢焦点。</summary>
        private void BeginRowRename(FrameworkElement textBlock, CustomMask mask)
        {
            if (textBlock.Parent is not Grid row)
                return;

            if (row.FindName("RowNameBox") is not TextBox box)
                return;

            if (row.FindName("RowNameText") is not TextBlock label)
                return;

            // 先把上一行收起来，避免两行同时处于编辑态
            CancelRowRename();

            box.Text = mask.Name;
            _renamingId = mask.Id;
            _renamingBox = box;

            label.Visibility = Visibility.Collapsed;
            box.Visibility = Visibility.Visible;

            LogService.Debug($"[掩码窗口] 开始改名：{mask.Name}（{mask.Id}）");

            box.Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// 放弃改名：收起输入框，文字恢复为原值，不落盘、不弹窗。
        ///
        /// 失焦、Esc、点空白、切行、点取消、关窗都走这里。
        /// 「只有回车才算确认」是刻意设计 —— 之前用失焦提交，
        /// 结果点取消 / 关窗都会因为失焦触发校验，反过来弹「名称不可用」把用户堵住。
        /// </summary>
        private void CancelRowRename()
        {
            TextBox? box = _renamingBox;
            if (box == null)
                return;

            _renamingBox = null;
            _renamingId = null;

            if (box.Parent is Grid row)
            {
                if (row.FindName("RowNameText") is TextBlock label)
                    label.Visibility = Visibility.Visible;
            }

            box.Visibility = Visibility.Collapsed;
            box.Tag = null;
            box.ToolTip = null;

            LogService.Debug("[掩码窗口] 取消改名");
        }

        private void RowNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box)
                return;

            if (e.Key == Key.Enter)
            {
                CommitRowRename(box, silent: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRowRename();
                e.Handled = true;
            }
        }

        private void RowNameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // 失焦 = 静默提交。
            //
            // 为什么不是「失焦=放弃」：用户手上有输入时点到「保存」，必须先收下这串文字，
            // 否则刚改的名字会被悄悄丢掉。而「静默」是指 —— 名字不可用时**不弹窗**，
            // 直接回退成原名。之前弹「名称不可用」正是点取消 / 关窗也被堵住的元凶。
            //
            // 真正要「放弃」的场合（Esc、点列表空白、点取消、关窗）都由各自的
            // 入口先调 CancelRowRename 把 _renamingBox 清空，这里自然就不会触发。
            if (sender is TextBox box && ReferenceEquals(box, _renamingBox))
                CommitRowRename(box, silent: true);
        }

        /// <summary>
        /// 输入即裁剪：超过权重上限的部分直接去掉，光标留在末尾。
        ///
        /// 用户明确要求「超过的部分裁掉」，所以这里不再只做红框提示。
        /// 中文输入法组字期间 TextChanged 会带着拼音字母触发，
        /// 裁剪可能打断一次组字 —— 但这是「长度硬上限」必然的代价，
        /// 比起让人先打完再被拒，边打边裁更符合直觉。
        /// </summary>
        private void RowNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_trimming || sender is not TextBox box)
                return;

            string text = box.Text ?? string.Empty;
            if (MaskNamePolicy.Weight(text) <= MaskNamePolicy.MaxWeight)
                return;

            string trimmed = MaskNamePolicy.TrimToWeight(text, MaskNamePolicy.MaxWeight);
            _trimming = true;
            try
            {
                box.Text = trimmed;
                box.CaretIndex = trimmed.Length;
            }
            finally
            {
                _trimming = false;
            }

            LogService.Debug($"[掩码窗口] 名称超限已裁剪：\"{text}\" → \"{trimmed}\"");
        }

        /// <summary>
        /// 确认改名。
        /// </summary>
        /// <param name="silent">
        /// true = 名字不合法时静默回退（失焦路径）；
        /// false = 弹窗提示（回车路径，用户主动确认，需要知道为什么没生效）。
        /// </param>
        private void CommitRowRename(TextBox box, bool silent = false)
        {
            string name = (box.Text ?? string.Empty).Trim();
            string? id = _renamingId;

            CustomMask? mask = id == null
                ? null
                : _rows.FirstOrDefault(r => r.Id == id);

            CancelRowRename();

            if (mask == null)
                return;

            // 重名检查按 Id 排除自己：不改名直接回车不该被判重名；
            // 草稿的 Id 不在 MaskStore 里，所以它对自己的名字没有豁免 —— 正确，
            // 草稿与已保存条目同名时必须报冲突。
            if (!MaskNamePolicy.IsValid(name, MaskStore.NamesExceptId(mask.Id), out string error))
            {
                if (!silent)
                    DarkDialog.ShowWarning(error, "名称不可用");

                LogService.Debug($"[掩码窗口] 名称不可用，已回退：{error}");
                return;
            }

            if (string.Equals(name, mask.Name, StringComparison.Ordinal))
                return;

            // 草稿只改内存，不写盘
            if (ReferenceEquals(mask, _draft))
            {
                mask.Name = name;
                _pendingName = name;
                RefreshList();
                LogService.Debug($"[掩码窗口] 草稿改名：{name}（未落盘）");
                return;
            }

            string oldName = mask.Name;
            mask.Name = name;
            if (!MaskStore.Upsert(mask, out MaskStore.UpsertError err))
            {
                mask.Name = oldName;   // 写盘被拒，内存里也要回滚，否则界面与文件不一致
                DarkDialog.ShowWarning(DescribeError(err), "改名失败");
                RefreshList();
                return;
            }

            _pendingName = name;
            LogService.Debug($"[掩码窗口] 改名已落盘：{oldName} → {name}");
            RefreshList();
        }

        /// <summary>把存储层错误码翻成给人看的一句话。</summary>
        private static string DescribeError(MaskStore.UpsertError err) => err switch
        {
            MaskStore.UpsertError.BadName
                => $"名称不可用（{MaskNamePolicy.Hint}）。",
            MaskStore.UpsertError.DuplicateName
                => MaskNamePolicy.DuplicateError,
            MaskStore.UpsertError.TooMany
                => $"自定义掩码最多 {MaskStore.MaxCount} 套，已达上限。",
            _ => "写入未能完成，请重试。"
        };

        // ---------- 按钮 ----------

        /// <summary>
        /// 新建：只在界面里加一条**草稿**，不写盘。
        /// 用户点「保存」才真正创建；点取消或直接关窗，草稿随窗口一起消失。
        ///
        /// 满 10 套时按钮已置灰；这里再拦一道 —— 键盘回车等路径可能绕过按钮状态。
        /// </summary>
        private void New_Click(object sender, RoutedEventArgs e)
        {
            CancelRowRename();

            if (_draft != null)
            {
                // 已经有草稿，直接回到它上面继续编辑
                SelectAndRename(_draft);
                return;
            }

            if (MaskStore.IsFull)
            {
                DarkDialog.ShowWarning(
                    $"自定义掩码最多 {MaskStore.MaxCount} 套，当前已满。\n删除一条不用的掩码后即可新建。",
                    "已达上限");
                return;
            }

            CustomMask draft = new() { Name = SuggestDraftName(), Mask = 0 };
            _draft = draft;
            _editingId = draft.Id;
            _pendingName = draft.Name;

            ApplyMask(0);
            RefreshList();
            SelectAndRename(draft);

            LogService.Debug($"[掩码窗口] 新建草稿：{draft.Name}（{draft.Id}），尚未落盘");
        }

        /// <summary>
        /// 建议的新建名称：「自定义掩码」「自定义掩码 2」…
        ///
        /// 必须跳过所有已占用名称，否则会出现两条同名条目（历史上最多出现到
        /// 两条「自定义掩码 1」并存）。数字后缀从 2 起 —— 「自定义掩码」本身
        /// 就是第 1 个。
        /// </summary>
        private string SuggestDraftName()
        {
            const string baseName = "自定义掩码";

            // Set 里包含已落盘 + 草稿，忽略大小写（与重名校验同一口径）
            HashSet<string> taken = new(MaskStore.Masks.Select(m => m.Name.Trim()),
                                        StringComparer.OrdinalIgnoreCase);

            if (_draft != null)
                taken.Add(_draft.Name.Trim());

            // 数字后缀从 2 起 ——「自定义掩码」本身就是第 1 个
            return Enumerable.Range(1, int.MaxValue)
                .Select(n => n == 1 ? baseName : $"{baseName} {n}")
                .First(name => !taken.Contains(name));
        }

        private void SelectAndRename(CustomMask row)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MaskList.SelectedItem = row;
                MaskList.ScrollIntoView(row);
                MaskList.UpdateLayout();

                if (MaskList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container)
                {
                    TextBlock? label = FindDescendant<TextBlock>(container, "RowNameText");
                    if (label != null)
                        BeginRowRename(label, row);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>在可视树里按名字找后代元素（DataTemplate 内的具名元素只能这样取）。</summary>
        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T fe && fe.Name == name)
                    return fe;

                T? found = FindDescendant<T>(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>行内删除图标：删的是这一行对应的掩码，与当前选中项无关。</summary>
        private void RowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CustomMask mask)
            {
                DeleteMask(mask);
                e.Handled = true;
            }
        }

        private void DeleteMask(CustomMask mask)
        {
            LogService.Debug($"[掩码窗口] 请求删除：{mask.Name}（{mask.Id}）");

            if (!DarkDialog.Confirm($"确定删除掩码「{mask.Name}」？", "删除确认"))
            {
                LogService.Debug("[掩码窗口] 删除已取消");
                return;
            }

            CancelRowRename();

            // 删草稿：直接从内存丢掉，不需要碰磁盘
            if (ReferenceEquals(mask, _draft))
            {
                _draft = null;
                ResetEditor();
                RefreshList();
                LogService.Debug("[掩码窗口] 草稿已丢弃");
                return;
            }

            MaskStore.Remove(mask.Id);

            if (_editingId == mask.Id)
                ResetEditor();

            LogService.Info($"已删除自定义掩码「{mask.Name}」");
            RefreshList();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            CancelRowRename();

            // 保存的对象 = 当前选中的行（可能是草稿，也可能是已存在的条目）
            CustomMask? row = _editingId == null
                ? null
                : _rows.FirstOrDefault(r => r.Id == _editingId);

            if (row == null)
            {
                DarkDialog.ShowWarning("请先点「新建」或从左侧选择一条掩码。", "没有可保存的掩码");
                return;
            }

            string name = string.IsNullOrWhiteSpace(row.Name) ? _pendingName : row.Name;

            // 重名检查按 Id 排除自己：重新保存一条没有改名的掩码，
            // 不该被自己的旧名字挡住。
            if (!MaskNamePolicy.IsValid(name, MaskStore.NamesExceptId(row.Id), out string error))
            {
                // 保存是用户主动动作，这里必须明确告知原因
                DarkDialog.ShowWarning(error, "名称不可用");
                return;
            }

            CollectMask(out ulong mask);
            if (mask == 0)
            {
                DarkDialog.ShowWarning("请至少勾选一个核心 / 线程。", "掩码为空");
                return;
            }

            string oldName = row.Name;
            row.Name = name.Trim();
            row.Mask = mask;

            if (!MaskStore.Upsert(row, out MaskStore.UpsertError err))
            {
                row.Name = oldName;
                DarkDialog.ShowWarning(DescribeError(err), "保存失败");
                return;
            }

            bool wasDraft = ReferenceEquals(row, _draft);
            if (wasDraft)
                _draft = null;

            _editingId = row.Id;
            _pendingName = row.Name;
            RefreshList();

            LogService.Info($"已保存自定义掩码「{row.Name}」（{row.CoreCount} 个线程）");
            LogService.Debug($"[掩码窗口] 保存：{(wasDraft ? "草稿→落盘" : "更新已有")}，mask=0x{mask:X}");
            DarkDialog.ShowSuccess($"已保存掩码「{row.Name}」。", "保存成功");
        }

        /// <summary>全部核心：所有逻辑处理器（含 T1）全勾。</summary>
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox box in _allBoxes)
                box.IsChecked = true;
        }

        /// <summary>
        /// 物理核心：每核只留 T0，T1（SMT 兄弟线程）全部取消。
        /// Tag 里存的是逻辑处理器序号；AMD SMT 下 T1 = T0 + 物理核数，
        /// 所以「序号小于物理核数」就是 T0，比按界面文字匹配可靠。
        /// </summary>
        private void SelectPhysCore_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox box in _allBoxes)
            {
                int logical = (int)box.Tag;
                box.IsChecked = logical < _physicalCores;
            }
        }

        /// <summary>反向选择：每个勾选框取反。全勾时按一次变全不选，再按回到全勾。</summary>
        private void Invert_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox box in _allBoxes)
                box.IsChecked = box.IsChecked != true;
        }

        /// <summary>
        /// 取消 / 右上角关闭：先明确放弃正在进行的改名，再关窗。
        /// 顺序很重要 —— 先 Cancel 会把 _renamingBox 清空，
        /// 这样随后由焦点转移触发的 LostKeyboardFocus 就不会再去提交。
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            LogService.Debug("[掩码窗口] 用户请求关闭（取消/叉）");
            CancelRowRename();
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
