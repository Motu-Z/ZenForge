using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zentwik.Controls
{
    /// <summary>
    /// 重排时带位移过渡的虚拟化纵向面板。
    ///
    /// 【为什么需要一个面板，而不是在 ViewModel 里做缓动】
    /// 进程列表每 2 秒按占用重排一次，行位置的变化发生在布局层：
    /// ObservableCollection.Move 之后，ItemsControl 重新 Arrange，行直接落到新位置。
    /// 数据是离散的（第 3 行变第 7 行），中间没有「第 4、5、6 行」这种中间态，
    /// 所以任何在数据层做的平滑都补不出位移 —— 过渡只能发生在 Arrange 之后。
    ///
    /// 做法：先让基类按新顺序摆好（拿到最终位置），再把每行的渲染位置
    /// 拉回它上一次所在的位置，然后动画归零。布局值始终是新的，
    /// 眼睛看到的是旧位置 → 新位置的过程。
    /// </summary>
    public class SmoothSortPanel : VirtualizingStackPanel
    {
        /// <summary>过渡时长。超过 300ms 会被读成「卡了一下」，低于 150ms 又看不出过程。</summary>
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(200);

        /// <summary>小于半像素的位移不做动画：那是四舍五入的抖动，不是换位。</summary>
        private const double MinSlide = 0.5;

        /// <summary>
        /// 兜底的位移上限（逻辑像素），只在拿不到视口高度时使用。
        ///
        /// 【为什么不能只用一个常量】
        /// 「超过一屏就不补间」这个判断里的「一屏」不是常数：它随窗口大小和高 DPI 变化。
        /// 写死 480 时，一个 1440p 窗口 + 150% 缩放下列表视口能有 900+ 逻辑像素，
        /// 正常的换位也会被判成「从天而降」而无动画 —— 阈值一旦小于一屏，
        /// 功能就变成时灵时不灵。所以下面的 <see cref="SlideLimit"/> 优先用视口高度，
        /// 这个常量只在视口还不可知时兜底。
        /// </summary>
        private const double FallbackMaxSlide = 480d;

        /// <summary>视口高度的这个比例之内才补间（留 10% 余量给半露出的行）。</summary>
        private const double ViewportSlideRatio = 0.9d;

        /// <summary>滚动后这么久内不做过渡（毫秒），避开惯性滚动期。</summary>
        private const int ScrollSuppressMs = 220;

        /// <summary>
        /// 每个子项上一次的落点。
        ///
        /// 【必须用 ConditionalWeakTable】
        /// 虚拟化回收会不断丢弃容器（滚出视野即被回收或复用）。用普通 Dictionary
        /// 以容器为键，等于把每个出现过的容器都强引用住 —— 列表滚一圈就泄漏一屏，
        /// 正是本工程踩过的坑。ConditionalWeakTable 的键是弱引用，
        /// 容器被回收时条目自动消失，不需要任何清理逻辑。
        /// </summary>
        private readonly ConditionalWeakTable<UIElement, Slot> _slots = new();

        private long _suppressUntilTick;
        private ScrollViewer? _host;
        private double _viewportHeight;

        /// <summary>「Loaded 时没找到 ScrollViewer，已排了一次重试」的标记，避免每次 Loaded 都排。</summary>
        private bool _retryScheduled;

        static SmoothSortPanel()
        {
            // 面板行为不随主题变化，只覆写元数据让样式系统不必再给一遍默认值。
            IsVirtualizingProperty.OverrideMetadata(typeof(SmoothSortPanel),
                new FrameworkPropertyMetadata(true));
            VirtualizationModeProperty.OverrideMetadata(typeof(SmoothSortPanel),
                new FrameworkPropertyMetadata(VirtualizationMode.Recycling));
        }

        public SmoothSortPanel()
        {
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>是否启用重排过渡。关掉即退化成普通 VirtualizingStackPanel（v1.1.x 的降级开关）。</summary>
        public static readonly DependencyProperty IsReorderAnimationEnabledProperty =
            DependencyProperty.Register(nameof(IsReorderAnimationEnabled), typeof(bool),
                typeof(SmoothSortPanel), new FrameworkPropertyMetadata(true));

        public bool IsReorderAnimationEnabled
        {
            get => (bool)GetValue(IsReorderAnimationEnabledProperty);
            set => SetValue(IsReorderAnimationEnabledProperty, value);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size arranged = base.ArrangeOverride(finalSize);
            TrackPositions();
            return arranged;
        }

        /// <summary>
        /// 用户主动触发重排（点列头排序）时调用：清掉滚动抑制窗口，让这一次换位必定有过渡。
        /// </summary>
        /// <remarks>
        /// 滚动与排序可以前后脚发生（刚滚完就点表头）。抑制窗口本来是给滚动用的，
        /// 用户的排序动作优先级更高，所以这里直接把它清掉即可。
        /// </remarks>
        public static void RequestSmooth(DependencyObject? root)
        {
            ClearSuppression(root);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_host != null)
                return;

            // 滚动时所有行的位置都在变，那是位移动画最不该出现的时刻：
            // 行会「追」着手指跑。找到滚动宿主并订阅，滚动期间一律跳过过渡。
            ScrollViewer? host = FindScrollHost();
            if (host != null)
            {
                Hook(host);
                return;
            }

            // 【为什么这里必须重试】
            // ItemsPanel 是在 ItemsControl 首次布局时才被实例化的，Loaded 触发的那一刻
            // 它的祖先链可能还没搭完（ScrollViewer 尚未成为父级）。按原来的写法
            // 「找不到就 return」，面板会永久停留在没订阅的状态 —— 于是滚动期间
            // 不做抑制，行追着滚动条跑，而这个功能存在的唯一理由就是防止这件事。
            //
            // 排到 Loaded 优先级再试一次：那时可视树已经稳定，代价只是一帧。
            // 重试只排一次，重复 Loaded 不会堆积委托。
            if (_retryScheduled)
                return;

            _retryScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _retryScheduled = false;
                if (_host != null)
                    return;

                ScrollViewer? retry = FindScrollHost();
                if (retry != null)
                    Hook(retry);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 移出可视树时解绑。
        ///
        /// ScrollViewer 通过 ScrollChanged 强引用本面板，而它是面板的祖先 ——
        /// 同生共死时无所谓，但 ItemsControl 换 ItemsPanel、或面板被移出树而
        /// ScrollViewer 还活着（换页时并不罕见）时，这个引用会把面板多留一会儿，
        /// 连带它那张 ConditionalWeakTable 一起。解绑后状态归零，下次 Loaded 重新挂。
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();

        private void Hook(ScrollViewer host)
        {
            _host = host;
            _viewportHeight = host.ViewportHeight;
            host.ScrollChanged += OnScrollChanged;
        }

        private void Unhook()
        {
            if (_host != null)
            {
                _host.ScrollChanged -= OnScrollChanged;
                _host = null;
            }

            _viewportHeight = 0;
            _suppressUntilTick = 0;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 视口高度会随窗口大小变化，跟着更新，位移上限才不会停在旧尺寸上。
            if (sender is ScrollViewer host)
                _viewportHeight = host.ViewportHeight;

            if (Math.Abs(e.VerticalChange) > MinSlide || Math.Abs(e.HorizontalChange) > MinSlide)
                _suppressUntilTick = Environment.TickCount64 + ScrollSuppressMs;
        }

        /// <summary>本次补间允许的最大位移：视口高度的九成，拿不到视口时用兜底常量。</summary>
        private double SlideLimit =>
            _viewportHeight > 0 ? _viewportHeight * ViewportSlideRatio : FallbackMaxSlide;

        private void TrackPositions()
        {
            bool allow = IsReorderAnimationEnabled && Environment.TickCount64 >= _suppressUntilTick;
            double limit = SlideLimit;

            UIElementCollection children = InternalChildren;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not UIElement child)
                    continue;

                Vector offset = VisualTreeHelper.GetOffset(child);
                object? data = (child as FrameworkElement)?.DataContext;

                Slot slot = _slots.GetValue(child, static _ => new Slot());

                if (!slot.HasPosition)
                {
                    // 首次出现：没有「上一次」，无从补间。只记录起点。
                    slot.Remember(offset, data);
                    continue;
                }

                double dy = slot.Y - offset.Y;

                if (!allow || !slot.SameData(data) || Math.Abs(dy) < MinSlide || Math.Abs(dy) > limit)
                {
                    // 三种不该动的情况：滚动刚过、容器被复用给了别的行、位移超出一屏。
                    // 都必须先把残留的偏移清干净，否则复用的容器会带着上一次的位移出现。
                    Reset(child);
                    slot.Remember(offset, data);
                    continue;
                }

                Slide(child, slot.X - offset.X, dy);
                slot.Remember(offset, data);
            }
        }

        /// <summary>
        /// 把渲染位置拉回旧位置，再动画归零。
        ///
        /// 【为什么 base 值恒为 0 + FillBehavior.Stop】
        /// 动画跑完自己回到 base 值，所以 base 必须是终点 0。这样既不需要
        /// Completed 回调去清 transform，也不会留下一个 HoldEnd 的时钟长期挂在行上
        /// —— 而 Stop 的时钟跑完就释放，容器被虚拟化回收时不会带着偏移去下一行。
        /// </summary>
        private static void Slide(UIElement child, double dx, double dy)
        {
            if (child.RenderTransform is not TranslateTransform tt)
            {
                // 行上若已有别的变换（将来的折叠/展开动画），不抢它的 RenderTransform。
                if (child.RenderTransform != null)
                    return;

                tt = new TranslateTransform();
                child.RenderTransform = tt;
            }

            CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(dx, 0, SlideDuration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(dy, 0, SlideDuration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease });
        }

        private static void Reset(UIElement child)
        {
            if (child.RenderTransform is not TranslateTransform tt)
                return;

            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.X = 0;
            tt.Y = 0;
        }

        private ScrollViewer? FindScrollHost()
        {
            DependencyObject? node = this;
            while (node != null)
            {
                node = VisualTreeHelper.GetParent(node);
                if (node is ScrollViewer sv)
                    return sv;
            }
            return null;
        }

        /// <summary>
        /// 清掉子树里**每一个**面板的滚动抑制窗口。
        ///
        /// 【为什么不是「找到第一个就返回」】
        /// 进程页有两个列表（全部 / 已绑定），各有一份 ItemsPanel。原来的写法返回
        /// 先遇到的那一个，而「先遇到」取决于 XAML 里的声明顺序，不一定是此刻
        /// 显示的那个 —— 结果是：点表头排序，用户看到的那个列表没有过渡，
        /// 而隐藏的那个白白被清了一次。两个列表互斥显示，全清没有副作用。
        ///
        /// 递归整棵子树的开销只在「用户点了列头」这一次发生，不值得为它维护引用。
        /// </summary>
        private static void ClearSuppression(DependencyObject? root)
        {
            if (root == null)
                return;

            if (root is SmoothSortPanel panel)
                panel._suppressUntilTick = 0;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                ClearSuppression(VisualTreeHelper.GetChild(root, i));
        }

        /// <summary>某行上一次的落点与它当时绑定的数据。</summary>
        private sealed class Slot
        {
            public double X;
            public double Y;
            public bool HasPosition;

            /// <summary>弱引用持有 DataContext：容器被复用给别的行时要能认出来，又不能延长数据存活。</summary>
            private WeakReference? _data;

            public void Remember(Vector offset, object? data)
            {
                X = offset.X;
                Y = offset.Y;
                HasPosition = true;
                _data = data == null ? null : new WeakReference(data);
            }

            /// <summary>这次的 DataContext 与上次是否同一份（容器没被复用给别人）。</summary>
            public bool SameData(object? data)
            {
                if (_data == null)
                    return data == null;

                return _data.Target != null && ReferenceEquals(data, _data.Target);
            }
        }
    }
}
