using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 日志页。
    /// </summary>
    public partial class LogPage : Page
    {
        private bool _subscribed;
        private DispatcherTimer? _followTimer;

        public LogPage()
        {
            InitializeComponent();
            UpdateCount();

            // 订阅/解绑成对绑定在 Loaded/Unloaded 上。
            // 早先只在构造函数里 +=、在 Unloaded 里 -=：页面离开一次后
            // 就再也不订阅了，回到日志页时不再自动跟随新条目、计数也不刷新
            // （Frame 默认不缓存页面，每次进入都是新实例，但旧实例的 -= 会作用在
            //   同一个静态事件上——等于把后来实例的订阅也一起摘掉了）。
            Loaded += (_, _) => Subscribe();
            Unloaded += (_, _) => Unsubscribe();
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            LogService.EntryAdded += OnEntryAdded;
            _subscribed = true;
            SyncVerboseToggle();
            UpdateCount();
        }

        private void Unsubscribe()
        {
            StopFollow();

            if (!_subscribed) return;
            LogService.EntryAdded -= OnEntryAdded;
            _subscribed = false;
        }

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

        private void OnEntryAdded(LogEntry entry)
        {
            // EntryAdded 本来就在 UI 线程触发（LogService 内部已封送），
            // 再 BeginInvoke 一次只是白白多排一次队。
            UpdateCount();

            if (FollowToggle.IsChecked != true)
                return;

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

            // 页面已经卸载（或日志页不在前台）时不要再碰列表
            if (!_subscribed || !IsLoaded)
                return;

            try
            {
                if (FollowToggle.IsChecked != true || LogListBox.Items.Count == 0)
                    return;

                LogListBox.SelectedIndex = LogListBox.Items.Count - 1;
                LogListBox.ScrollIntoView(LogListBox.SelectedItem);
            }
            catch
            {
                // 页面正在卸载时控件可能已经不在可视树里
            }
        }

        private void StopFollow()
        {
            if (_followTimer != null && _followTimer.IsEnabled)
                _followTimer.Stop();
        }

        private void UpdateCount()
        {
            LogCountText.Text = $"共 {LogListBox.Items.Count} 条";
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            // 先停掉跟随再清空：否则队列里那次 ScrollIntoView 会在集合已经空了
            // 之后才执行，对着一个不存在的项做定位，界面就此卡住。
            StopFollow();

            // 摘掉数据源再清。ObservableCollection.Clear() 会让 ListBox 一次性丢弃
            // 所有已生成的容器；列表还绑着的时候做这件事，既有开销，又容易和队列里
            // 那半拍 ScrollIntoView 打架 —— 之前「点清空就卡死」就是这一路的现场。
            // 先断开，清完再挂回来：界面这次只是「换了一份空数据」，不涉及容器回收。
            System.Collections.IEnumerable? source = LogListBox.ItemsSource;
            LogListBox.ItemsSource = null;

            try
            {
                LogService.Clear();
            }
            finally
            {
                LogListBox.ItemsSource = source ?? LogService.Entries;
            }

            UpdateCount();
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
    }
}
