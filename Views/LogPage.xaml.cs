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

        /// <summary>
        /// 暂停状态直接读 <see cref="LogService.Paused"/>，不在页面里另存一份。
        /// 两份状态一定会不同步：用户从托盘或别处改了，页面这份就永远停在旧值。
        /// </summary>
        private bool Paused => LogService.Paused;

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
            Loaded += (_, _) => Subscribe();
            Unloaded += (_, _) => Unsubscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            LogService.EntryAdded += OnEntryAdded;
            _subscribed = true;

            // 暂停状态是进程级的（LogService.Paused），离开页面再回来仍是暂停态。
            // 不在这里同步按钮文案的话，界面会显示「暂停日志记录」而实际已在暂停，
            // 点一下反而变成"暂停"——按钮说的和做的不一致。
            PauseButton.Content = LogService.Paused ? "恢复日志记录" : "暂停日志记录";

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
        ///
        /// 【版式对齐】
        /// 高度 28 与右侧三个工具按钮严格一致（LogToolButtonStyle 也是 28），
        /// 两者都 VerticalAlignment=Center，落在同一条水平线上。
        /// 旧版 pill 26 / 按钮 32，胶囊上下各悬空 3px，读起来像
        /// 「四个胶囊飘在一条更粗的带子里」而不是一行工具栏。
        /// </summary>
        private void BuildPills()
        {
            PillHost.Children.Clear();

            foreach ((string label, LogLevel? level, string dotKey, string? tintKey) in PillSpecs)
            {
                Border pill = new()
                {
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(12, 0, 12, 0),
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

                // 7px 级别色点：比旧版 6px 略大，因为 pill 本身抬高了 2px，
                // 圆点不跟着长会显得「缩在胶囊里」。左边距 0、右边 7，
                // 文字与圆点靠得比文字与胶囊边缘更近，读起来是一个整体。
                inner.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Res(dotKey, Brushes.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0),
                });

                TextBlock text = new()
                {
                    FontSize = 12,
                    FontWeight = FontWeights.Normal,
                    Foreground = Res(dotKey, Brushes.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                inner.Children.Add(text);
                pill.Child = inner;
                pill.MouseLeftButtonUp += Pill_Click;

                PillHost.Children.Add(pill);
            }
        }

        /// <summary>
        /// 按当前计数刷新 pill 文案与选中态（选中 = 描边加粗 + 不降透明度）。
        ///
        /// 【这里不再遍历 Entries】
        /// 旧实现每刷新一次就把整个集合走一遍。日志页每收到一条新日志都要刷新，
        /// 绑核一次产生几十条就是几十次全量扫描，全在 UI 线程上 ——
        /// 这是「日志页越用越卡」的真正原因，比渲染开销大得多。
        /// 现在数字直接从 LogService 的增量计数读，O(1)。
        /// </summary>
        private void UpdateStats()
        {
            // 顺序必须与 PillSpecs 一一对应：信息 / 警告 / 错误 / 致命
            long[] counts =
            {
                LogService.CountInfo,
                LogService.CountWarn,
                LogService.CountError,
                LogService.CountCritical,
            };

            for (int i = 0; i < PillHost.Children.Count && i < PillSpecs.Length; i++)
            {
                if (PillHost.Children[i] is not Border pill || pill.Child is not StackPanel inner)
                    continue;

                if (inner.Children.Count < 2 || inner.Children[1] is not TextBlock text)
                    continue;

                (string label, LogLevel? level, string dotKey, string? tintKey) = PillSpecs[i];
                text.Text = $"{label} {counts[i]}";

                bool active = _levelFilter == level;
                bool dimmed = _levelFilter != null && !active;

                // 【选中态只改「填充」，不改边框粗细】
                // 旧版选中时把 BorderThickness 从 1 改成 2 —— 边框内缩 1px，
                // 胶囊里的文字跟着左右各抖 1px，点一下四个 pill 都在微跳。
                // 现在粗细恒定 1，靠底色从 tint(12%) 换成实心级别色来表达选中，
                // 尺寸完全不动。
                pill.Opacity = dimmed ? 0.45 : 1.0;

                Brush levelBrush = Res(dotKey, Brushes.Gray);
                Brush tintBrush = Res(tintKey ?? "Input.Background", Brushes.Transparent);

                // 选中时文字与圆点转深色：实心黄底 / 红底上用同级色写字等于消失。
                Brush onLevelBrush = active ? Brushes.Black : levelBrush;

                pill.Background = active ? levelBrush : tintBrush;
                pill.BorderBrush = levelBrush;

                if (inner.Children[0] is System.Windows.Shapes.Ellipse dot)
                    dot.Fill = onLevelBrush;

                text.Foreground = onLevelBrush;
            }

            // 副行：条数 + 暂停状态。
            // 暂停时必须在副行显式说明，否则用户看到"共 500 条"却一直不涨，
            // 会以为是日志功能坏了 —— 而实际是他自己点了暂停。
            int total = LogService.Count;
            string headline = total == 0
                ? "本次会话暂无记录"
                : $"本次会话共捕获 {total} 条";

            LogSubText.Text = LogService.Paused
                ? headline + "　·　已暂停记录，新日志会被丢弃"
                : headline;
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
            if (Paused)
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

            if (Paused)
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

            if (!_subscribed || !IsLoaded || Paused)
                return;

            ScrollToLatest();
        }

        private void StopFollow()
        {
            if (_followTimer != null && _followTimer.IsEnabled)
                _followTimer.Stop();
        }

        // ==================== 搜索框 ====================

        /// <summary>
        /// 搜索词变化。
        ///
        /// 占位符与图标由 zenforge:SearchBox 自己维护（见 Themes/SearchBox.xaml），
        /// 页面只负责「重建可见列表」。
        /// 上一版在页面里管占位符，失焦后还要 BeginInvoke 延后一拍才能读到正确的
        /// IsKeyboardFocusWithin —— 那是在替控件解决它自己的问题。现在没有这个问题了。
        /// </summary>
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ScheduleRebuild();

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

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            bool paused = !LogService.Paused;
            LogService.Paused = paused;

            PauseButton.Content = paused ? "恢复日志记录" : "暂停日志记录";

            if (paused)
            {
                // 暂停后 Write() 在入口就 return，OnEntryAdded 不会再被调用，
                // 列表保持原样，副行会改写成「已暂停记录」。
                StopFollow();

                // 这条自己的日志在置 Paused 之后写，会被丢弃 —— 这是预期的：
                // 暂停就是暂停，不能为了一条"我暂停了"的记录破例。
                LogService.Info("日志记录已暂停（新日志不再写入内存与文件）");
                UpdateStats();
                return;
            }

            // 恢复：重建一次，保证界面与集合一致
            RebuildVisibleList();
            LogService.Info("日志记录已恢复");
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            // 【为什么不给二次确认】
            // 清空的是"日志"，不是用户数据。日志丢了不会造成任何不可恢复的损失，
            // 反而每次都要点两下会让人不愿意清。真正需要确认的是全局重置那种操作。

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

            // 磁盘那半是后台删的，这里只报"已发出"，不假装已经删完。
            // Clear() 内部把文件删除丢给 Task.Run，UI 线程上拿不到结果。
            ToastService.Info("已清空界面记录与日志文件");
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
