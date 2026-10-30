using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZenForge.Helpers;

namespace ZenForge.Views
{
    /// <summary>
    /// 轻量级深色弹窗，用来替代无法改色的系统 MessageBox。
    ///
    /// 用法（与 MessageBox 接近，可原位替换）：
    ///   DarkDialog.Show("写入成功");
    ///   DarkDialog.Show("写入成功", "ZenForge · FMax");
    ///   DarkDialog.ShowWarning("参数越界", "ZenForge");
    ///   DarkDialog.ShowError("回读不一致", "ZenForge · 写入失败");
    ///   if (DarkDialog.Confirm("确定要写入吗？", "ZenForge")) { ... }
    ///
    /// 线程安全：可从任意线程调用，内部自动封送到 UI 线程。
    /// </summary>
    public partial class DarkDialog : Window
    {
        public enum DialogKind
        {
            Info,
            Success,
            Warning,
            Error,
            Question
        }

        private DarkDialog()
        {
            InitializeComponent();

            UiTextOptions.Attach(this);

            // 兜底清除焦点虚线框：本窗口是独立 Window，不属于主窗口可视树，
            // 且按钮用的是内联模板，App 层的可视树清扫覆盖不到这里。
            Loaded += (_, _) => SweepFocusVisuals(this);
            ContentRendered += (_, _) => SweepFocusVisuals(this);
        }

        /// <summary>递归清掉本窗口内所有元素的 FocusVisualStyle。</summary>
        private static void SweepFocusVisuals(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element && element.FocusVisualStyle != null)
                {
                    try
                    {
                        element.FocusVisualStyle = null;
                    }
                    catch
                    {
                        // 只读元素，忽略
                    }
                }
                SweepFocusVisuals(child);
            }
        }

        /// <summary>图标配色：符号色 + 圆底色（浅色主题用同色系浅调、深色主题用原色）。</summary>
        private readonly record struct IconPalette(string Glyph, string SoftFill, string DeepInk)
        {
            public static IconPalette For(DialogKind kind) => kind switch
            {
                DialogKind.Success => new("\u2713", "#16C60C", "#0B6B06"),  // ✓ 绿
                DialogKind.Warning => new("!", "#FFB900", "#5C4200"),       // ! 黄
                DialogKind.Error => new("\u2715", "#C42B1C", "#7A1810"),    // ✕ 红
                _ => new(kind == DialogKind.Question ? "?" : "i", "#38BDF8", "#0B4A66")
            };
        }

        // ==================== 静态入口 ====================

        /// <summary>信息提示（青色 i 图标 + 单个“确定”）。</summary>
        public static void Show(string message, string title = "ZenForge")
            => ShowCore(message, title, DialogKind.Info);

        /// <summary>成功提示（绿色对勾）。</summary>
        public static void ShowSuccess(string message, string title = "ZenForge")
            => ShowCore(message, title, DialogKind.Success);

        /// <summary>警告提示（橙色感叹号）。</summary>
        public static void ShowWarning(string message, string title = "ZenForge")
            => ShowCore(message, title, DialogKind.Warning);

        /// <summary>错误提示（红色叉）。</summary>
        public static void ShowError(string message, string title = "ZenForge")
            => ShowCore(message, title, DialogKind.Error);

        /// <summary>确认框，返回 true = 用户点了“确定”。</summary>
        public static bool Confirm(string message, string title = "ZenForge")
        {
            bool result = false;
            Invoke(() =>
            {
                DarkDialog dialog = Create(message, title, DialogKind.Question, showCancel: true);
                result = dialog.ShowDialog() == true;
            });
            return result;
        }

        // ==================== 内部实现 ====================

        private static void ShowCore(string message, string title, DialogKind kind)
            => Invoke(() => Create(message, title, kind, showCancel: false).ShowDialog());

        /// <summary>把委托封送到 UI 线程执行（无 Application 时直接执行）。</summary>
        private static void Invoke(Action action)
        {
            Application? app = Application.Current;
            if (app == null)
            {
                action();
                return;
            }

            if (app.Dispatcher.CheckAccess())
                action();
            else
                app.Dispatcher.Invoke(action);
        }

        private static DarkDialog Create(string message, string title, DialogKind kind, bool showCancel)
        {
            DarkDialog dialog = new();

            dialog.TitleTextBlock.Text = string.IsNullOrWhiteSpace(title) ? "ZenForge" : title;

            // 短文本会被 SizeToContent 压成一条细缝，给个空格保住行高
            dialog.MessageTextBlock.Text = string.IsNullOrEmpty(message) ? " " : message;

            dialog.CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

            ApplyIcon(dialog, IconPalette.For(kind));

            // 居中于主窗口。
            // 必须用 App.RealMainWindow：托盘菜单那个 1×1 宿主也是 Window 且是可见的，
            // 拿它当 Owner 会把弹窗整体摆到屏幕坐标 (-10000,-10000) 上去。
            Window? owner = App.RealMainWindow;
            if (owner != null && !ReferenceEquals(owner, dialog) && owner.IsVisible)
                dialog.Owner = owner;

            return dialog;
        }

        /// <summary>
        /// 按主题上色。原先这里是「白字 + 彩色圆底」：深色主题下好看，浅色主题下
        /// 白色对号压在浅底上几乎看不见。改成底色取同色系浅调、符号取同色系深调，
        /// 两套主题下对比度都够。
        /// </summary>
        private static void ApplyIcon(DarkDialog dialog, IconPalette palette)
        {
            bool light = IsLightTheme();

            dialog.IconGlyph.Text = palette.Glyph;
            dialog.IconGlyph.FontWeight = FontWeights.Bold;
            dialog.IconGlyph.Foreground = Brush(light ? palette.DeepInk : "#FFFFFF");
            dialog.IconBadge.Background = Brush(light ? LightenHex(palette.SoftFill) : palette.SoftFill);
        }

        private static SolidColorBrush Brush(string hex) =>
            new((Color)ColorConverter.ConvertFromString(hex));

        /// <summary>当前是否处于浅色主题（决定弹窗图标的配色方案）。</summary>
        private static bool IsLightTheme()
        {
            try
            {
                object? v = Application.Current?.TryFindResource("Text.Primary");
                if (v is SolidColorBrush b)
                {
                    // 前景色偏亮 = 深色主题；偏暗 = 浅色主题
                    double lum = (0.2126 * b.Color.R + 0.7152 * b.Color.G + 0.0722 * b.Color.B) / 255.0;
                    return lum < 0.5;
                }
            }
            catch { /* 主题资源不可用时按深色处理 */ }
            return false;
        }

        /// <summary>把颜色往白色方向提亮，用于浅色主题下的图标底。</summary>
        private static string LightenHex(string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            // 与白色按 0.72 混合：足够浅，但仍能看出是绿/黄/红/青
            byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * 0.72);
            return $"#{Mix(c.R):X2}{Mix(c.G):X2}{Mix(c.B):X2}";
        }

        // ==================== 交互 ====================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 鼠标已释放，忽略
                }
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>Esc 相当于取消 / 关闭。</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }
    }
}
