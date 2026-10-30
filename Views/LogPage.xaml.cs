using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 日志页（v12）。
    ///
    /// 结构：页头（标题 + 条数副行 + 三个工具按钮）→ 级别 pill 组 → 搜索框 → 日志卡片列表。
    ///
    /// 数据链路刻意做成「三段」，不要合并：
    ///   LogService.Entries（全部，上限 2000）
    ///     → <see cref="VisibleEntries"/>（过滤后的可见集合，绑定给 ListBox）
    ///     → pill 计数（对 Entries 全量统计，不随过滤变化）
    /// 为什么 pill 不跟着过滤走：pill 的作用是「告诉我总量里各有多少」，
    /// 如果点一个 WARN 筛选后 INFO 的计数变成 0，用户会以为日志丢了。
    ///
    /// 为什么需要独立的可见集合而不是直接用 CollectionViewSource：
    /// LogService.Entries 是从后台线程写入的（内部已封送到 UI 线程），
    /// 直接在它上面挂 Filter 会让「新增一条」触发一次全量重过滤；
    /// 而日志页常常一次冲进来几十条（绑核一次就是几十行），
    /// 独立集合可以按批处理，一次重建完再统一刷新。
    /// </summary>
    public partial class LogPage : Page
    {
        /// <summary>绑定给列表的可见集合（已按级别 + 搜索词过滤）。</summary>
        public static ObservableCollection<LogEntry> VisibleEntries { get; } = new();

        private bool _subscribed;
        private DispatcherTimer? _followTimer;

        /// <summary>过滤条件变化后延迟合并重建，避免连续输入时每条日志都重建一次。</summary>
        private DispatcherTimer? _rebuildTimer;

        /// <summary>暂停：只停止界面刷新，LogService 仍照常写文件。</summary>
        private bool _paused;

        /// <summary>当前级别筛选（null = 全部）。点同一枚 pill 再点一次可取消。</summary>
        private LogLevel? _levelFilter;

        public LogPage()
        {
            InitializeComponent();
            BuildPills();
            RebuildVisibleList();

            // 订阅/解绑成对绑定在 Loaded/Unloaded 上。
            // 早先只在构造函数里 +=、在 Unloaded 里 -=：页面离开一次后
            // 就再也不订阅了，回到日志页时不再自动跟随新条目、计数也不刷新
            Loaded += (_, _) =>
            {
                Subscribe();
                SyncVerboseToggle();
            };
            Unloaded += (_, _) => Unsubscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            LogService.EntryAdded += OnEntryAdded;
            _subscribed = true;
            UpdateStats();
        }

        private void Unsubscribe()
        {
            StopFollow();

            if (!_subscribed) return;
            LogService.EntryAdded -= OnEntryAdded;
            _subscribed = false;
        }

        // ==================== 级别 pill ====================

        /// <summary>
        /// 四枚 pill 的生成顺序与配色（信息 / 警告 / 错误 / 致命）。
        ///
        /// 【为什么去掉「调试」换成「致命」】
        /// 调试级只在 Verbose 打开时才有内容，常规运行下永远显示 0 ——
        /// 一枚「按下去永远是空列表」的筛选器，占着最左边最显眼的位置。
        /// 换成「致命」后，异常兜底记录的崩溃信息有了独立入口，
        /// 而且四项正好对应四个固定语义色：Log.Info / Warn / Error / Critical。
        ///
        /// 【配色铁律】这四个 DotKey 全部指向 Palette 里的硬编码语义色，
        /// 不跟随 AccentBrush。用户改强调色时，警告还是黄、错误还是红。
        /// </summary>
        private static readonly (string Label, LogLevel? Level, string DotKey, string? TintKey)[] PillSpecs =
        {
            ("信息", LogLevel.Info,     "Log.Info",     "Log.InfoTint"),
            ("警告", LogLevel.Warn,     "Log.Warn",     "Log.WarnTint"),
            ("错误", LogLevel.Error,    "Log.Error",    "Log.ErrorTint"),
            ("致命", LogLevel.Critical, "Log.Critical", "Log.CriticalTint"),
        };

        /// <summary>
        /// 生成四枚 pill 并挂点击筛选。
        ///
        /// 用代码生成而不是 XAML 写死四份：四个 pill 的结构完全相同，
        /// 差别只有颜色和级别枚举，写四份模板会让「加一级」变成改五处。
        /// </summary>
        private void BuildPills()
        {
            PillHost.Children.Clear();

            foreach ((string label, LogLevel? level, string dotKey, string? tintKey) in PillSpecs)
            {
                Border pill = new()
                {
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Padding = new Thickness(14, 0, 14, 0),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    Tag = level,
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true,
                };

                // 配色：全部走资源字典，深浅主题各自取值
                pill.Background = Res(tintKey ?? "Input.Background", Brushes.Transparent);
                pill.BorderBrush = Res(tintKey != null ? dotKey : "Input.Border", Brushes.Transparent);

                StackPanel inner = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                // 6px 圆点
                inner.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Res(dotKey, Brushes.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                });

                TextBlock text = new()
                {
                    FontSize = 12,
                    Foreground = Res(dotKey, Brushes.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                inner.Children.Add(text);
                pill.Child = inner;
                pill.MouseLeftButtonUp += Pill_Click;

                PillHost.Children.Add(pill);
            }
        }

        /// <summary>按当前计数刷新 pill 文案与选中态（选中 = 描边加粗 + 不降透明度）。</summary>
        private void UpdateStats()
        {
            int info = 0, warn = 0, error = 0, critical = 0;
            foreach (LogEntry e in LogService.Entries)
            {
                // 必须把四个级别全部列全。原实现用 `default: info++` 兜底，
                // 新增 Critical 时会被静默算进「信息」——致命错误显示成 Info 数量 +1，
                // 是那种不报错、但统计永远对不上的隐蔽 bug。
                switch (e.Level)
                {
                    case LogLevel.Warn: warn++; break;
                    case LogLevel.Error: error++; break;
                    case LogLevel.Critical: critical++; break;
                    case LogLevel.Info: info++; break;
                    // Debug 不计入任何 pill（没有对应筛选器），但仍计入下方总数。
                    default: break;
                }
            }

            // 顺序必须与 PillSpecs 一一对应：信息 / 警告 / 错误 / 致命
            int[] counts = { info, warn, error, critical };

            for (int i = 0; i < PillHost.Children.Count && i < PillSpecs.Length; i++)
            {
                if (PillHost.Children[i] is not Border pill || pill.Child is not StackPanel inner)
                    continue;

                if (inner.Children.Count < 2 || inner.Children[1] is not TextBlock text)
                    continue;

                (string label, LogLevel? level, string dotKey, string? tintKey) = PillSpecs[i];
                text.Text = $"{label} {counts[i]}";

                bool active = _levelFilter == level;
                // 未选中时降到 55% 不透明度，四枚里只有一枚是"亮的"
                pill.Opacity = active || _levelFilter == null ? 1.0 : 0.55;
                pill.BorderThickness = new Thickness(active ? 2 : 1);
                pill.BorderBrush = Res(tintKey != null ? dotKey : "Input.Border", Brushes.Transparent);
            }

            // 副行：本次会话共 N 条（含被过滤掉的部分）
            int total = LogService.Entries.Count;
            LogSubText.Text = total == 0
                ? "本次会话暂无记录"
                : $"本次会话共捕获 {total} 条";
        }

        /// <summary>点 pill：切换级别筛选。再点一次同一枚 = 取消筛选。</summary>
        private void Pill_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border pill || pill.Tag is not LogLevel level)
                return;

            _levelFilter = _levelFilter == level ? null : level;
            ScheduleRebuild();
            e.Handled = true;
        }

        // ==================== 列表重建 ====================

        /// <summary>把「过滤 + 重建」合并到一次 Dispatcher 调用里，避免连续输入时反复重建。</summary>
        private void ScheduleRebuild()
        {
            _rebuildTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(120),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _rebuildTimer?.Stop();
                    RebuildVisibleList();
                },
                Dispatcher);

            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        /// <summary>
        /// 重建可见集合。
        ///
        /// 先用局部 List 组装好再整体替换内容，而不是逐条 Remove/Insert：
        /// ObservableCollection 每变一次都会让 ListBox 走一轮变更通知，
        /// 2000 条逐条处理会把界面卡住几百毫秒。
        /// </summary>
        private void RebuildVisibleList()
        {
            if (_paused)
                return;

            string keyword = (SearchBox?.Text ?? string.Empty).Trim();

            IEnumerable<LogEntry> query = LogService.Entries;
            if (_levelFilter is LogLevel level)
                query = query.Where(x => x.Level == level);
            if (keyword.Length > 0)
                query = query.Where(x => Matches(x, keyword));

            List<LogEntry> matched = query.ToList();

            VisibleEntries.Clear();
            foreach (LogEntry entry in matched)
                VisibleEntries.Add(entry);

            EmptyHint.Visibility = VisibleEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = LogService.Entries.Count == 0
                ? "暂无日志"
                : "没有匹配的日志";

            UpdateStats();
            ScrollToLatest();
        }

        /// <summary>
        /// 搜索匹配：正文 + 级别名 + 时间（HH:mm:ss）。
        /// 支持多个空格分隔的关键词，全部命中才算匹配（AND 语义）。
        /// </summary>
        private static bool Matches(LogEntry entry, string keyword)
        {
            string[] parts = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string haystack = $"{entry.Message} {entry.LevelText} {entry.Time:HH:mm:ss}";

            foreach (string part in parts)
            {
                if (haystack.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        private void ScrollToLatest()
        {
            if (VisibleEntries.Count == 0)
                return;

            try
            {
                LogListBox.SelectedIndex = VisibleEntries.Count - 1;
                LogListBox.ScrollIntoView(LogListBox.SelectedItem);
            }
            catch
            {
                // 页面正在卸载时控件可能已经不在可视树里
            }
        }

        // ==================== 新日志到达 ====================

        private void OnEntryAdded(LogEntry entry)
        {
            UpdateStats();

            if (_paused)
                return;

            // 单条到达时不整体重建（重建会丢滚动位置），只判断是否命中当前过滤条件
            string keyword = (SearchBox?.Text ?? string.Empty).Trim();
            bool levelOk = _levelFilter is not LogLevel level || entry.Level == level;
            bool textOk = keyword.Length == 0 || Matches(entry, keyword);

            if (levelOk && textOk)
            {
                VisibleEntries.Add(entry);
                EmptyHint.Visibility = Visibility.Collapsed;
            }

            // 突发日志（绑一次核就是几十条）逐条 ScrollIntoView 会把每一行
            // 都强制实例化并布局一遍，界面跟着顿。合并成 200ms 一次跟随。
            _followTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(200),
                DispatcherPriority.Background,
                OnFollowTick,
                Dispatcher);

            _followTimer.Start();
        }

        private void OnFollowTick(object? sender, EventArgs e)
        {
            StopFollow();

            if (!_subscribed || !IsLoaded || _paused)
                return;

            ScrollToLatest();
        }

        private void StopFollow()
        {
            if (_followTimer != null && _followTimer.IsEnabled)
                _followTimer.Stop();
        }

        // ==================== 搜索框 ====================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchPlaceholder();
            ScheduleRebuild();
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => UpdateSearchPlaceholder();

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e) => UpdateSearchPlaceholder();

        private void UpdateSearchPlaceholder()
        {
            if (SearchPlaceholder == null || SearchBox == null)
                return;

            SearchPlaceholder.Visibility =
                string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Esc 清空搜索；已经是空的时候把焦点还给列表。</summary>
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Clear();
                e.Handled = true;
            }
            else
            {
                LogListBox.Focus();
                e.Handled = true;
            }
        }

        // ==================== 工具栏 ====================

        /// <summary>
        /// 把开关状态同步成日志服务的真实状态。
        /// 开关不持久化 —— 详细日志是排查工具，不该在用户不知情时长期开着，
        /// 下次启动回到默认（诊断版为开、常规版为关）。
        /// </summary>
        private void SyncVerboseToggle()
        {
            if (VerboseToggle != null)
                VerboseToggle.IsChecked = LogService.Verbose;
        }

        private void VerboseToggle_Click(object sender, RoutedEventArgs e)
        {
            bool on = VerboseToggle.IsChecked == true;
            LogService.Verbose = on;

            // 这条 Info 一定会落盘，作为「从此刻起有详细日志」的分界线
            LogService.Info(on
                ? "详细日志已开启（本次运行有效）"
                : "详细日志已关闭");
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            PauseButton.Content = _paused ? "继续" : "暂停";

            if (_paused)
            {
                StopFollow();
                LogService.Info("日志界面已暂停刷新（文件仍在写入）");
                return;
            }

            // 恢复时整体重建一次，把暂停期间攒下的条目一次性补上
            RebuildVisibleList();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            // 先停掉跟随再清空：否则队列里那次 ScrollIntoView 会在集合已经空了
            // 之后才执行，对着一个不存在的项做定位，界面就此卡住。
            StopFollow();

            // 摘掉数据源再清。ListBox 一次性丢弃所有已生成的容器时既有开销，
            // 又容易和队列里那半拍 ScrollIntoView 打架 —— 「点清空就卡死」就是这一路。
            System.Collections.IEnumerable? source = LogListBox.ItemsSource;
            LogListBox.ItemsSource = null;

            try
            {
                VisibleEntries.Clear();
                LogService.Clear();
            }
            finally
            {
                LogListBox.ItemsSource = source ?? VisibleEntries;
            }

            UpdateStats();
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "暂无日志";
        }

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", LogService.LogDirectory)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"打开日志目录失败: {ex.Message}");
            }
        }

        // ==================== 资源查找 ====================

        /// <summary>
        /// 取画刷，找不到时回落给定值。
        ///
        /// 走 TryFindResource 而不是索引器：pill 的配色键（Log.InfoTint 等）
        /// 是 v12 新增的，旧的主题册里可能还没有 —— 直接索引会抛异常。
        /// </summary>
        private static Brush Res(string key, Brush fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }
}
