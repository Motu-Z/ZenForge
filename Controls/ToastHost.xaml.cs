using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZenForge.Services;

namespace ZenForge.Controls
{
    /// <summary>提示类型。决定左侧色条与图标形状，颜色全部取自语义色资源。</summary>
    public enum ToastKind
    {
        Success,
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// Toast 宿主：主窗口顶部居中的轻量提示层。
    ///
    /// 【为什么不用 MessageBox / DarkDialog 做普通反馈】
    /// 模态弹窗的代价是「你必须先处理我，才能继续」：
    ///   · 抢焦点 —— 用户正在输入框里打字会被打断；
    ///   · 阻塞 —— 必须点「确定」，连续调参时要多点十几下；
    ///   · 重 —— 一条「已保存」用弹窗承载，视觉重量远大于事情本身。
    /// 保存配置、加载配置这类「做成了，知道了就行」的反馈，Toast 才是对的形态。
    ///
    /// 只有三种场景仍然用模态：
    ///   1. 破坏性操作确认（全局重置）；
    ///   2. 需要用户做决定的警告（严重越界参数）；
    ///   3. 失败且需要用户了解原因（写入失败、未处理异常）。
    ///
    /// 【为什么图标用 Path 而不是字体符号】
    /// 字体符号（✓ ✕ !）在不同机器上字体回退不同，有的会渲染成豆腐块。
    /// Path 是矢量，任何 DPI 下都一致。
    /// </summary>
    public partial class ToastHost : UserControl
    {
        /// <summary>默认驻留时长。足够读完一句 20 字左右的提示。</summary>
        private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3);

        /// <summary>鼠标悬停时的最长驻留：不主动消失，但也不永久占位。</summary>
        private static readonly TimeSpan HoverDwell = TimeSpan.FromSeconds(9);

        public ToastHost()
        {
            InitializeComponent();

            Loaded += (_, _) => ToastService.Host = this;
            Unloaded += (_, _) =>
            {
                if (ReferenceEquals(ToastService.Host, this))
                    ToastService.Host = null;
            };
        }

        /// <summary>
        /// 显示一条提示。可从任意线程调用（内部封送回 UI 线程）。
        /// </summary>
        public void Show(string message, ToastKind kind)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Show(message, kind)));
                return;
            }

            Border card = BuildCard(message, kind);

            // 最多同时堆 3 条。再多就把最旧的挤掉 ——
            // 屏幕上叠四五条提示会盖住页面标题，反而不如只留最近几条。
            while (Items.Children.Count >= 3 && Items.Children[0] is FrameworkElement oldest)
                Items.Children.Remove(oldest);

            Items.Children.Add(card);
            PlayEnter(card);
            ScheduleExit(card);
        }

        // ---------------- 视觉构造 ----------------

        private Border BuildCard(string message, ToastKind kind)
        {
            (string barKey, string glyph, Geometry geometry) = Describe(kind);

            // 左侧 3px 色条：占满卡片高度，用 Border 而不是 Rectangle 方便贴圆角
            Border bar = new()
            {
                Width = 3,
                Background = Res(barKey),
                CornerRadius = new CornerRadius(2, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            Path icon = new()
            {
                Data = geometry,
                Stroke = Res(barKey),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(12, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            TextBlock text = new()
            {
                Text = message,
                FontSize = 13,
                Foreground = Res("Text.Primary"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 19,
                Margin = new Thickness(0, 0, 16, 0),
            };

            DockPanel content = new() { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Left);
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(bar);
            content.Children.Add(icon);
            content.Children.Add(text);

            Border card = new()
            {
                Child = content,
                Background = Res("Toast.Background"),
                BorderBrush = Res("Toast.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(0, 12, 0, 12),
                Margin = new Thickness(0, 0, 0, 8),
                MaxWidth = 460,
                MinWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 2,
                    Direction = 270,
                    Opacity = 0.45,
                    Color = Colors.Black,
                },
                // 先透明，由 PlayEnter 动画推上来；否则第一帧会闪一下完整卡片
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, -16),
            };

            // 悬停延长驻留：3 秒对「我要看清这句话」偏短。
            // 用一个附加状态在 ScheduleExit 里读取（见下）。
            card.MouseEnter += (_, _) => card.Tag = "hover";
            card.MouseLeave += (_, _) => card.Tag = null;

            return card;
        }

        private static (string ColorKey, string Glyph, Geometry Geometry) Describe(ToastKind kind) => kind switch
        {
            ToastKind.Success => ("Log.Info",
                "✓", Geometry.Parse("M 3,8.5 L 6.5,12 L 13,4")),
            ToastKind.Warning => ("Log.Warn",
                "!", Geometry.Parse("M 8,3 L 8,9.5 M 8,12.5 L 8,13")),
            ToastKind.Error => ("Log.Error",
                "✕", Geometry.Parse("M 4,4 L 12,12 M 12,4 L 4,12")),
            _ => ("Log.Info",
                "i", Geometry.Parse("M 8,3.5 L 8,4.5 M 8,7 L 8,13")),
        };

        // ---------------- 动画 ----------------

        private static void PlayEnter(FrameworkElement card)
        {
            Duration d = new(TimeSpan.FromMilliseconds(200));
            CubicEase ease = new() { EasingMode = EasingMode.EaseOut };

            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, d) { EasingFunction = ease });

            if (card.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-16, 0, d) { EasingFunction = ease });
            }
        }

        /// <summary>
        /// 安排淡出。用 DispatcherTimer 而不是 Task.Delay：
        /// Task 的续体会跑到线程池，虽然最终还是封送回 UI，但取消语义更麻烦；
        /// DispatcherTimer 天然在 UI 线程，且 Stop 即取消。
        /// </summary>
        private void ScheduleExit(FrameworkElement card)
        {
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
            TimeSpan elapsed = TimeSpan.Zero;

            timer.Tick += (_, _) =>
            {
                // 鼠标停在上面就不计时：用户在读它，不能让它跑掉
                if (card.Tag as string == "hover")
                {
                    if (elapsed > HoverDwell)
                    {
                        timer.Stop();
                        PlayExit(card);
                    }
                    return;
                }

                elapsed += timer.Interval;
                if (elapsed < Dwell)
                    return;

                timer.Stop();
                PlayExit(card);
            };

            timer.Start();
        }

        private void PlayExit(FrameworkElement card)
        {
            Duration d = new(TimeSpan.FromMilliseconds(250));
            CubicEase ease = new() { EasingMode = EasingMode.EaseIn };

            DoubleAnimation fade = new(1, 0, d) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                // 动画已结束，必须先把动画从属性上摘掉再移除元素，
                // 否则 WPF 会保留动画时钟对一个已脱离可视树的元素做时钟推进。
                card.BeginAnimation(OpacityProperty, null);
                Items.Children.Remove(card);
            };

            card.BeginAnimation(OpacityProperty, fade);

            if (card.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, -8, d) { EasingFunction = ease });
            }
        }

        private Brush Res(string key)
        {
            if (TryFindResource(key) is Brush brush)
                return brush;

            // 资源缺失时用中性灰兜底，绝不抛：Toast 是提示层，
            // 它自己崩溃比提示不显示严重得多。
            return Brushes.Gray;
        }
    }
}
