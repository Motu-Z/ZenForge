using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZenForge.Controls
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
        /// 位移超过一屏就不做动画。
        /// 虚拟化下容器是复用的：从列表底部滚上来的容器可能「上次」在 800px 外，
        /// 真按这个差值补间会看到一行从天而降横穿整屏，比瞬切更刺眼。
        /// </summary>
        private const double MaxSlide = 480d;

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
        private bool _scrollHooked;

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
            if (FindPanel(root) is SmoothSortPanel panel)
                panel._suppressUntilTick = 0;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_scrollHooked)
                return;

            // 滚动时所有行的位置都在变，那是位移动画最不该出现的时刻：
            // 行会「追」着手指跑。找到滚动宿主并订阅，滚动期间一律跳过过渡。
            ScrollViewer? host = FindScrollHost();
            if (host == null)
                return;

            host.ScrollChanged += OnScrollChanged;
            _scrollHooked = true;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.VerticalChange) > MinSlide || Math.Abs(e.HorizontalChange) > MinSlide)
                _suppressUntilTick = Environment.TickCount64 + ScrollSuppressMs;
        }

        private void TrackPositions()
        {
            bool allow = IsReorderAnimationEnabled && Environment.TickCount64 >= _suppressUntilTick;

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

                if (!allow || !slot.SameData(data) || Math.Abs(dy) < MinSlide || Math.Abs(dy) > MaxSlide)
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

        private static SmoothSortPanel? FindPanel(DependencyObject? root)
        {
            if (root == null)
                return null;

            if (root is SmoothSortPanel panel)
                return panel;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                SmoothSortPanel? found = FindPanel(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                    return found;
            }
            return null;
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
