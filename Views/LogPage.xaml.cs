using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 日志页（v1.0.2）。
    ///
    /// 结构：页头 → 工具卡（级别 pill + 详细日志 / 搜索 + 暂停·清空·目录）→ 列表卡。
    ///
    /// 数据链路刻意做成「三段」，不要合并：
    ///   LogService.Entries（全部，上限 500）
    ///     → <see cref="VisibleEntries"/>（过滤后的可见集合，绑定给 ListBox）
    ///     → pill 计数（对 Entries 全量统计，不随过滤变化）
    /// 为什么 pill 不跟着过滤走：pill 的作用是「告诉我总量里各有多少」，
    /// 如果点一个「警告」筛选后「信息」的计数变成 0，用户会以为日志丢了。
    ///
    /// 为什么需要独立的可见集合而不是直接用 CollectionViewSource：
    /// LogService.Entries 是从后台线程写入的（内部已封送到 UI 线程），
    /// 直接在它上面挂 Filter 会让「新增一条」触发一次全量重过滤；
    /// 而日志页常常一次冲进来几十条（绑核一次就是几十行），
    /// 独立集合可以按批处理，一次重建完再统一刷新。
    /// </summary>
    public partial class LogPage : Page, IPageLifecycle, IDisposable
    {
        /// <summary>本页在路由中的 key，与 MainWindow 侧栏的 Tag 一致。</summary>
        public const string PageKey = "logs";

        /// <summary>绑定给列表的可见集合（已按级别 + 搜索词过滤）。</summary>
        public static ObservableCollection<LogEntry> VisibleEntries { get; } = new();

        /// <summary>
        /// 暂停状态直接读 <see cref="LogService.Paused"/>，不在页面里另存一份。
        /// 两份状态一定会不同步：用户从托盘或别处改了，页面这份就永远停在旧值。
        /// </summary>
        private bool Paused => LogService.Paused;

        /// <summary>当前级别筛选（null = 全部）。点同一枚 pill 再点一次可取消。</summary>
        private LogLevel? _levelFilter;

        private bool _subscribed;

        /// <summary>过滤条件变化后延迟合并重建，避免连续输入时每条日志都重建一次。</summary>
        private ManagedTimer? _rebuildTimer;

        /// <summary>突发日志到达时延迟跟随，避免几十条逐条 ScrollIntoView。</summary>
        private ManagedTimer? _followTimer;

        /// <summary>
        /// pill 互斥时的重入保护。
        /// 程序化地把另外两枚置 false 会触发它们的 Unchecked，
        /// 而那一次不是「用户取消了筛选」—— 没有这个闸门，
        /// 从「信息」切到「警告」会顺手把筛选条件一起清空。
        /// </summary>
        private bool _syncingPills;

        private ToggleButton[] Pills => new[] { PillInfo, PillWarn, PillError };

        public LogPage()
        {
            InitializeComponent();

            // 详细日志不持久化：它是排障时的临时开关，
            // 不该在下一次启动还开着、把日志写满磁盘。
            // 这里回填的是本次运行的实际状态。
            VerboseToggle.IsChecked = LogService.Verbose;

            RebuildVisibleList();
        }

        // ==================== 生命周期 ====================
        // 挂在路由器上，不再用 Loaded / Unloaded：那两个事件在页面实例被复用时
        // 只在第一次进视觉树时触发，第二次回到本页就不会再订阅。

        public void OnActivated()
        {
            Subscribe();

            // 离开期间产生的日志不会进 OnEntryAdded（那时没订阅），
            // 回来时补一次全量重建，否则界面停在离开前的最后一条。
            RebuildVisibleList();
        }

        public void OnDeactivated() => Unsubscribe();

        private void Subscribe()
        {
            if (_subscribed)
                return;

            LogService.EntryAdded += OnEntryAdded;
            _subscribed = true;

            // 暂停状态是进程级的（LogService.Paused），离开页面再回来仍是暂停态。
            // 不在这里同步按钮文案的话，界面会说「暂停」而实际已在暂停，
            // 点一下反而变成暂停 —— 按钮说的和做的不一致。
            PauseButton.Content = LogService.Paused ? "恢复" : "暂停";

            UpdateStats();
        }

        private void Unsubscribe()
        {
            StopFollow();

            if (!_subscribed)
                return;

            LogService.EntryAdded -= OnEntryAdded;
            _subscribed = false;
        }

        // ==================== 级别 pill ====================

        /// <summary>
        /// 点选一枚 pill：三枚互斥，选中态由 ToggleButton 自己持有。
        ///
        /// 【为什么筛选条件对 Error 与 Critical 同时生效】
        /// 致命级不再单独开一枚常驻 0 的胶囊，那就必须保证它不会被筛选漏掉，
        /// 否则「点错误 → 致命日志凭空消失」。列表里两者仍用各自的竖条颜色区分。
        /// </summary>
        private void Pill_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingPills)
                return;

            _syncingPills = true;
            foreach (ToggleButton pill in Pills)
                if (!ReferenceEquals(pill, sender))
                    pill.IsChecked = false;
            _syncingPills = false;

            _levelFilter = ReferenceEquals(sender, PillInfo) ? LogLevel.Info
                         : ReferenceEquals(sender, PillWarn) ? LogLevel.Warn
                         : LogLevel.Error;

            ScheduleRebuild();
        }

        /// <summary>再点一次已选中的 pill = 取消筛选（走到这里的一定是用户直接点的）。</summary>
        private void Pill_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_syncingPills)
                return;

            _levelFilter = null;
            ScheduleRebuild();
        }

        private static bool LevelMatches(LogEntry entry, LogLevel filter)
            => entry.Level == filter ||
               (filter == LogLevel.Error && entry.Level == LogLevel.Critical);

        /// <summary>
        /// 刷新三枚 pill 的计数文案。
        ///
        /// 【这里不再遍历 Entries】
        /// 旧实现每刷新一次就把整个集合走一遍。日志页每收到一条新日志都要刷新，
        /// 绑核一次产生几十条就是几十次全量扫描，全在 UI 线程上 ——
        /// 这是「日志页越用越卡」的真正原因，比渲染开销大得多。
        /// 现在数字直接从 LogService 的增量计数读，O(1)。
        /// </summary>
        private void UpdateStats()
        {
            PillInfo.Content = $"信息 {LogService.CountInfo}";
            PillWarn.Content = $"警告 {LogService.CountWarn}";

            // 致命合并进「错误」：显示为一个数，但两级的竖条颜色各自保留。
            PillError.Content = $"错误 {LogService.CountError + LogService.CountCritical}";
        }

        // ==================== 列表重建 ====================

        /// <summary>把「过滤 + 重建」合并到一次 Dispatcher 调用里，避免连续输入时反复重建。</summary>
        private void ScheduleRebuild()
        {
            _rebuildTimer ??= PageTimerManager.Create(
                PageKey,
                TimeSpan.FromMilliseconds(120),
                (_, _) =>
                {
                    _rebuildTimer?.Stop();
                    RebuildVisibleList();
                });

            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        /// <summary>
        /// 重建可见集合。
        ///
        /// 先用局部 List 组装好再整体替换内容，而不是逐条 Remove/Insert：
        /// ObservableCollection 每变一次都会让 ListBox 走一轮变更通知，
        /// 500 条逐条处理会把界面卡住几百毫秒。
        /// </summary>
        private void RebuildVisibleList()
        {
            if (Paused)
                return;

            string keyword = (SearchBox?.Text ?? string.Empty).Trim();

            IEnumerable<LogEntry> query = LogService.Entries;
            if (_levelFilter is LogLevel level)
                query = query.Where(x => LevelMatches(x, level));
            if (keyword.Length > 0)
                query = query.Where(x => Matches(x, keyword));

            List<LogEntry> matched = query.ToList();

            // 内容一字未变就别重建。
            // Clear + Add 会触发一次 Reset 通知，ListBox 随即重建可视容器、
            // 把滚动位置弹回顶部 —— 表现在界面上就是「闪一下 + 被拽回顶部」。
            if (SameEntries(matched))
            {
                UpdateStats();
                return;
            }

            // 重建会丢滚动位置：记住用户当时看到哪，重建后放回去。
            // 只有原本就贴在底部时才继续跟到最新一条（那才是用户期待的）。
            bool followLatest = IsAtBottom();
            double keepOffset = ListScrollViewer()?.VerticalOffset ?? 0;

            VisibleEntries.Clear();
            foreach (LogEntry entry in matched)
                VisibleEntries.Add(entry);

            RefreshEmptyHint();
            UpdateStats();

            if (followLatest)
                ScrollToLatest();
            else
                ListScrollViewer()?.ScrollToVerticalOffset(keepOffset);   // 用户正在往回翻历史，别把他拽回底部
        }

        /// <summary>当前可见集合与匹配结果是否完全一致（同一批对象、同一顺序）。</summary>
        private bool SameEntries(List<LogEntry> matched)
        {
            if (VisibleEntries.Count != matched.Count)
                return false;

            for (int i = 0; i < matched.Count; i++)
                if (!ReferenceEquals(VisibleEntries[i], matched[i]))
                    return false;

            return true;
        }

        private void RefreshEmptyHint()
        {
            EmptyHint.Visibility = VisibleEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = LogService.Entries.Count == 0 ? "暂无日志" : "没有匹配的日志";
        }

        /// <summary>
        /// 用户此刻是否贴着列表底部（容差 8px：滚动条是整数像素，浮点比较不可靠）。
        ///
        /// 判断这个是为了区分两种完全不同的意图：贴在底部 = 「跟着看最新」，
        /// 往上翻 = 「我在查历史，别动我的位置」。以前新日志一到就无条件
        /// ScrollToLatest，用户刚翻上去就被拽回底部，表现为整页在跳。
        /// </summary>
        private bool IsAtBottom()
        {
            ScrollViewer? sv = ListScrollViewer();
            if (sv == null)
                return true;      // 模板还没生成：按原行为（跟随）处理

            return sv.VerticalOffset >= sv.ScrollableHeight - 8;
        }

        private ScrollViewer? ListScrollViewer() => FindChild<ScrollViewer>(LogListBox);

        private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;

                if (FindChild<T>(child) is T deep)
                    return deep;
            }

            return null;
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
                if (haystack.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

            return true;
        }

        private void ScrollToLatest()
        {
            if (VisibleEntries.Count == 0)
                return;

            try
            {
                // 只滚动，不改选中项。
                // 以前这里顺手把最后一行选中，于是每来一条日志就有一块高亮
                // 底色跟着往下跳，并且会把用户手动选中的那行顶掉。
                LogListBox.ScrollIntoView(VisibleEntries[VisibleEntries.Count - 1]);
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
            bool levelOk = _levelFilter is not LogLevel level || LevelMatches(entry, level);
            bool textOk = keyword.Length == 0 || Matches(entry, keyword);

            if (levelOk && textOk)
            {
                VisibleEntries.Add(entry);
                EmptyHint.Visibility = Visibility.Collapsed;
            }

            // 突发日志（绑一次核就是几十条）逐条 ScrollIntoView 会把每一行
            // 都强制实例化并布局一遍，界面跟着顿。合并成 200ms 一次跟随。
            _followTimer ??= PageTimerManager.Create(
                PageKey,
                TimeSpan.FromMilliseconds(200),
                OnFollowTick);

            _followTimer.Start();
        }

        private void OnFollowTick(object? sender, EventArgs e)
        {
            StopFollow();

            if (!_subscribed || !IsLoaded || Paused)
                return;

            // 用户已经往回翻历史了就不要再把他拽下来。
            // 这一条是「日志页一直在跳」的主因：突发日志几十条时，
            // 跟随定时器每 200ms 把视图拉到底，用户根本没法读上面几行。
            if (!IsAtBottom())
                return;

            ScrollToLatest();
        }

        private void StopFollow() => _followTimer?.Stop();

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
                SearchBox.Clear();
            else
                LogListBox.Focus();

            e.Handled = true;
        }

        // ==================== 工具栏 ====================

        private void VerboseToggle_Checked(object sender, RoutedEventArgs e) => LogService.Verbose = true;

        private void VerboseToggle_Unchecked(object sender, RoutedEventArgs e) => LogService.Verbose = false;

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            bool paused = !LogService.Paused;
            LogService.Paused = paused;
            PauseButton.Content = paused ? "恢复" : "暂停";

            if (paused)
            {
                // 暂停后 Write() 在入口就 return，OnEntryAdded 不会再被调用，
                // 列表保持原样。
                StopFollow();
                UpdateStats();

                // 这条自己的日志在置 Paused 之后写，会被丢弃 —— 这是预期的：
                // 暂停就是暂停，不能为了一条「我暂停了」的记录破例。
                LogService.Info("日志记录已暂停（新日志不再写入内存与文件）");
                return;
            }

            RebuildVisibleList();
            LogService.Info("日志记录已恢复");
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            // 【为什么不给二次确认】
            // 清空的是「日志」，不是用户数据。日志丢了不会造成任何不可恢复的损失，
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

            // 磁盘那半是后台删的，这里只报「已发出」，不假装已经删完。
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

        /// <summary>页面被销毁（退出、全局重置）时释放本页的两个受管定时器。</summary>
        public void Dispose()
        {
            Unsubscribe();

            _rebuildTimer?.Dispose();
            _rebuildTimer = null;
            _followTimer?.Dispose();
            _followTimer = null;
        }
    }
}
