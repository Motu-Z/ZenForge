using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Zentwik.Controls
{
    /// <summary>
    /// 页面级搜索框：放大镜图标 + 输入框 + 占位文字，三者由 Grid 列定位。
    ///
    /// 【为什么抽成控件，而不是每个页面各写一遍】
    /// 之前进程页和日志页各抄一份「Grid 三列 + TextBox + 叠一层占位 TextBlock」，
    /// 抄出来的两份都错，而且错法不同：
    ///   · 进程页：占位符的 Margin 从资源里取 Thickness(32,0,0,0)，
    ///     但那 32 是「TextBox 内部 Padding」，不是「输入框距控件左缘的距离」；
    ///     真正的左内缩只有 Grid 第 0 列的 10 —— 于是占位文字比光标左移 32px，
    ///     直接压到放大镜下面。这就是截图里「图标被占位文字盖住」的原因。
    ///   · 日志页：同一个格子（Grid 第 2 列）里同时放 TextBox 和 TextBlock，
    ///     TextBlock 的 Margin 又在 TextBox 内部坐标上再加一次 32，
    ///     文字整体右移，看起来像「搜索框从中间开始」。
    /// 两份代码描述的是同一件事，却各自存了三份互相不一致的数值。
    /// 收进一个控件后，几何只有一个来源：
    ///
    ///     左留白 10（第 0 列） + 图标 14（第 1 列） + 间距 8（第 2 列左内缩）
    ///     = TextBox.Padding.Left = 32
    ///
    /// 占位符与 TextBox 处在同一格里，Margin 左值同样是 32 ——
    /// 两者相对同一个左缘内缩同样的距离，占位文字的第一个字
    /// 与光标起点必然重合，不存在「差几像素」的空间。
    ///
    /// 【占位符的显隐口径（v1.2.0 改）】
    /// 以前是「空 **且** 无焦点」才显示，结果用户点进搜索框就再也看不到提示文字，
    /// 于是反馈「搜索框没有占位符」。现在的口径是**只看有没有内容**：
    /// 空着就一直显示，一旦输入就消失 —— 与浏览器 / 系统搜索框一致。
    ///
    /// 光标会不会和占位文字打架？模板里占位符排在输入框**之前**，
    /// 也就是画在下层，光标画在上层。空框聚焦时看到的是「光标停在提示文字开头」，
    /// 而不是「提示文字盖住光标」。
    /// </summary>
    public class SearchBox : Control
    {
        // 几何常量（10 / 14 / 32）全部写在 Themes/SearchBox.xaml 的模板里。
        // 这里刻意不再保留一份 C# 常量：那份副本不会被模板消费，只会慢慢与模板脱节，
        // 而脱节的表现就是「文字与光标差几个像素」这种最难查的错位。

        static SearchBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(SearchBox), new FrameworkPropertyMetadata(typeof(SearchBox)));
        }

        /// <summary>
        /// 输入内容变化。
        ///
        /// 【为什么必须自己声明一个，而不是复用按下的 KeyDown】
        /// Control 继承自 UIElement，本来就有 KeyDown 事件；但这里需要的是
        /// 「文字变了」，而 TextBox.TextChanged 是 CLR 事件、无法冒泡，
        /// 所以由控件自己转一道。页面只订阅这一个事件即可拿到「用户改了搜索词」。
        /// </summary>
        public event TextChangedEventHandler? TextChanged;

        /// <summary>
        /// 内部输入框的按键（Esc 的语义由页面决定：清空 / 把焦点还给列表）。
        ///
        /// 【不能直接 RaiseEvent(e) 转发 KeyDown】
        /// 内部 TextBox 的 KeyDown 是路由事件，源就是那个 TextBox。
        /// 若在它自己的处理器里 RaiseEvent 同一个参数，事件会沿同一条路由再走一遍，
        /// 内部处理器再次被调用 —— 递归直到栈溢出。所以这里用一个独立的 CLR 事件转发。
        /// </summary>
        public event KeyEventHandler? InputKeyDown;

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(SearchBox),
                new FrameworkPropertyMetadata(string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

        /// <summary>输入内容。与内部 TextBox 双向同步。</summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // 外部（绑定 / 代码）赋值时要回灌到内部 TextBox；
            // 内部 TextBox 自己触发的那次已经被上面拦掉了，不会回环。
            if (d is SearchBox box
                && box._input != null
                && box._input.Text != (string?)e.NewValue)
            {
                box._input.Text = (string?)e.NewValue ?? string.Empty;
            }
        }

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(SearchBox),
                new PropertyMetadata(string.Empty));

        /// <summary>占位文字：仅当输入为空且未聚焦时显示。</summary>
        public string Placeholder
        {
            get => (string)GetValue(PlaceholderProperty);
            set => SetValue(PlaceholderProperty, value);
        }

        private TextBox? _input;
        private TextBlock? _placeholder;
        private Path? _icon;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            // 静态构造函数只跑一次，控件实例会被复用（页面缓存），
            // 每次套模板都要重新接线并解掉上一次的，否则重复订阅。
            if (_input != null)
            {
                _input.TextChanged -= OnInputTextChanged;
                _input.GotKeyboardFocus -= OnInputFocusChanged;
                _input.LostKeyboardFocus -= OnInputFocusChanged;
                _input.KeyDown -= OnInputKeyDown;
            }

            _input = GetTemplateChild("PART_Input") as TextBox;
            _placeholder = GetTemplateChild("PART_Placeholder") as TextBlock;
            _icon = GetTemplateChild("PART_Icon") as Path;

            if (_input != null)
            {
                _input.TextChanged += OnInputTextChanged;
                _input.GotKeyboardFocus += OnInputFocusChanged;
                _input.LostKeyboardFocus += OnInputFocusChanged;
                _input.KeyDown += OnInputKeyDown;

                // 【初始值为空时必须补一次回填】
                // 模板套用前 Text 可能已经由 XAML 赋过值，那次赋值进不了 TextBox。
                if (!string.IsNullOrEmpty(Text) && _input.Text != Text)
                    _input.Text = Text;
            }

            UpdatePlaceholder();
            UpdateIconColor();
        }

        /// <summary>把焦点交给内部的输入框（Ctrl+F 用）。</summary>
        public new bool Focus()
        {
            if (_input == null)
                return false;

            _input.Focus();
            return _input.IsKeyboardFocusWithin;
        }

        public void SelectAll() => _input?.SelectAll();

        public void Clear()
        {
            if (_input != null)
                _input.Clear();
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_input == null)
                return;

            if (Text != _input.Text)
                SetCurrentValue(TextProperty, _input.Text);

            UpdatePlaceholder();

            // 转发给页面。e 用原事件参数即可 —— 订阅方只关心「变了」。
            TextChanged?.Invoke(this, e);
        }

        private void OnInputFocusChanged(object sender, RoutedEventArgs e)
        {
            UpdatePlaceholder();
            UpdateIconColor();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            // 只转发给页面，不重新 RaiseEvent（见 InputKeyDown 的注释）。
            InputKeyDown?.Invoke(this, e);
        }

        /// <summary>
        /// 占位符：只有「没有内容」才显示，不看焦点。
        ///
        /// 判据取 _input.Text 而不是控件的 Text 属性：Text 走双向同步，
        /// 极端情况下（绑定延迟一帧）会比输入框慢半拍，占位符就会在已有文字时
        /// 还多留一帧，看起来像闪了一下。
        /// </summary>
        private void UpdatePlaceholder()
        {
            if (_placeholder == null || _input == null)
                return;

            _placeholder.Visibility = string.IsNullOrEmpty(_input.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>聚焦时放大镜换成强调色，作为「输入目标在这里」的轻提示。</summary>
        private void UpdateIconColor()
        {
            if (_icon == null || _input == null)
                return;

            object key = _input.IsKeyboardFocusWithin ? "AccentBrush" : "Text.MetaBrush";
            _icon.Stroke = TryFindResource(key) as Brush
                           ?? TryFindResource("Text.Primary") as Brush;
        }
    }
}
