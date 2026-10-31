using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace Zentwik.Services
{
    /// <summary>
    /// 页面级定时器的统一开关。
    ///
    /// 【为什么需要它】
    /// 定时器的生命周期一直是这个程序里最容易出错的地方：每个页面自己
    /// <c>new DispatcherTimer</c>、自己 Start、自己在 Loaded/Unloaded 里停。
    /// 结果是三件事反复发生：
    ///
    ///   · 页面切走了，定时器还在跑（UI 刷新循环在没人看的页面上空转）；
    ///   · 窗口最小化 / 收进托盘，定时器还在跑（后台白烧 CPU 与电）；
    ///   · 同一个页面被打开两次，定时器建了两份（越积越多，越跑越慢）。
    ///
    /// 这里把「调用方的意愿」和「当前允不允许跑」拆开：
    ///
    ///     调用方只表达意愿 —— Start() / Stop()
    ///     管理器决定实际状态 —— 意愿为真 且 页面是当前页 且 窗口可见
    ///
    /// 于是页面代码里再也看不到「判断自己是不是当前页」这种逻辑，
    /// 也不会因为忘记 Stop 而在后台空转。
    ///
    /// 【使用约定（基石）】
    ///   1. 页面内所有 DispatcherTimer 必须用 <see cref="Create"/> 创建，
    ///      不允许 new DispatcherTimer；
    ///   2. 常驻定时器在 OnActivated 里 Start（意愿），OnDeactivated 里 Stop；
    ///   3. 临时定时器（如「200ms 后跟随滚动」）照常 Start/Stop，
    ///      页面失活时会被自动挂起，回来时如果是 Start 状态会自动恢复；
    ///   4. 页面销毁时 Dispose 掉自己创建的全部 ManagedTimer。
    ///
    /// 全局定时器（主题轮询这类，不属于任何页面）用 <see cref="HostScope"/>，
    /// 它只受窗口可见性影响。
    /// </summary>
    public static class PageTimerManager
    {
        /// <summary>不属于任何页面、只随窗口可见性挂起的定时器用这个作用域。</summary>
        public const string HostScope = "*";

        private static readonly List<ManagedTimer> Timers = new();
        private static readonly object Gate = new();

        private static string? _activePage;
        private static bool _hostVisible = true;

        /// <summary>当前激活的页面 key（与路由使用同一套 key）。</summary>
        public static string? ActivePage => _activePage;

        /// <summary>
        /// 创建一个受管的定时器。创建后默认不启动，需要调用方 Start()。
        /// </summary>
        /// <param name="owner">所属页面 key；全局定时器用 <see cref="HostScope"/>。</param>
        /// <param name="interval">间隔。</param>
        /// <param name="tick">回调。</param>
        /// <param name="priority">
        /// 默认 Background：页面刷新属于「有空就做」，不该和输入、布局抢优先级。
        /// </param>
        public static ManagedTimer Create(
            string owner,
            TimeSpan interval,
            EventHandler tick,
            DispatcherPriority priority = DispatcherPriority.Background)
        {
            ManagedTimer timer = new(owner, interval, tick, priority);
            lock (Gate)
                Timers.Add(timer);

            timer.Sync(IsAllowed(owner));
            return timer;
        }

        /// <summary>页面切入：它的定时器恢复，其它页面的定时器全部挂起。</summary>
        public static void SetActivePage(string? pageKey)
        {
            _activePage = pageKey;
            SyncAll();
        }

        /// <summary>
        /// 宿主窗口可见性变化。隐藏 / 最小化 / 收进托盘时传 false。
        ///
        /// 这是「纯后台 0% CPU」的执行点：窗口看不见时，页面上任何一个
        /// 刷新定时器都不该继续跑。
        /// </summary>
        public static void SetHostVisible(bool visible)
        {
            if (_hostVisible == visible)
                return;

            _hostVisible = visible;
            SyncAll();
        }

        internal static void Remove(ManagedTimer timer)
        {
            lock (Gate)
                Timers.Remove(timer);
        }

        /// <summary>
        /// 收掉全部受管定时器。窗口销毁时调用。
        ///
        /// 页面在 OnDeactivated 里会 Dispose 自己的定时器，这条只是兜底：
        /// 漏掉一个就意味着静态表里留着一个 DispatcherTimer + 一个指向页面的
        /// 回调委托，窗口关了页面也回收不了。窗口都要销毁了，全部收掉最干净；
        /// 下次新建窗口时页面会重新 Create。
        /// </summary>
        public static void ClearAll()
        {
            ManagedTimer[] snapshot;
            lock (Gate)
            {
                snapshot = Timers.ToArray();
                Timers.Clear();
            }

            foreach (ManagedTimer timer in snapshot)
            {
                try { timer.Dispose(); }
                catch { /* 单个失败不影响其余 */ }
            }

            _activePage = null;
        }

        /// <summary>把所有定时器按当前条件同步一次（调试与自检用）。</summary>
        public static void SyncAll()
        {
            ManagedTimer[] snapshot;
            lock (Gate)
                snapshot = Timers.ToArray();

            foreach (ManagedTimer timer in snapshot)
                timer.Sync(IsAllowed(timer.Owner));
        }

        /// <summary>当前实际在跑的定时器数量（自检 / 日志用）。</summary>
        public static int RunningCount
        {
            get
            {
                lock (Gate)
                {
                    int count = 0;
                    foreach (ManagedTimer timer in Timers)
                        if (timer.IsRunning)
                            count++;

                    return count;
                }
            }
        }

        private static bool IsAllowed(string owner)
        {
            if (!_hostVisible)
                return false;

            return owner == HostScope || owner == _activePage;
        }
    }

    /// <summary>
    /// 受 <see cref="PageTimerManager"/> 管理的定时器包装。
    /// 对外只暴露「意愿」（Start / Stop），实际是否在跑由管理器决定。
    /// </summary>
    public sealed class ManagedTimer : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly EventHandler _tick;
        private bool _wanted;

        internal ManagedTimer(
            string owner,
            TimeSpan interval,
            EventHandler tick,
            DispatcherPriority priority)
        {
            Owner = owner;
            _tick = tick;

            _timer = new DispatcherTimer(priority, Dispatcher.CurrentDispatcher)
            {
                Interval = interval,
            };
            _timer.Tick += OnTick;
        }

        internal string Owner { get; }

        /// <summary>调用方是否希望它跑（与实际是否在跑无关）。</summary>
        public bool Wanted => _wanted;

        /// <summary>此刻是否真的在跑。</summary>
        public bool IsRunning => _timer.IsEnabled;

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        /// <summary>表达「我要它跑」。页面失活或窗口隐藏时会被自动挂起。</summary>
        public void Start()
        {
            _wanted = true;

            // 不直接 _timer.Start()：先记意愿，再由管理器按当前条件决定。
            // 否则「在后台页面里 Start」会真的跑起来一拍才会被停掉。
            PageTimerManager.SyncAll();
        }

        /// <summary>表达「我不要它跑」。这个是硬停止，不受管理器条件影响。</summary>
        public void Stop()
        {
            _wanted = false;
            _timer.Stop();
        }

        /// <summary>由管理器调用：按「意愿 + 当前条件」决定实际状态。</summary>
        internal void Sync(bool allowed)
        {
            if (_wanted && allowed)
            {
                if (!_timer.IsEnabled)
                    _timer.Start();
            }
            else if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        private void OnTick(object? sender, EventArgs e) => _tick(sender, e);

        public void Dispose()
        {
            _wanted = false;
            _timer.Stop();
            _timer.Tick -= OnTick;
            PageTimerManager.Remove(this);
        }
    }
}
