using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Zentwik.App.Controls;

/// <summary>
/// 数值步进器：<c>[−] 数值 [+]</c>。
///
/// 【为什么不用系统 NumberBox】
/// NumberBox 的模板里 <c>InputBox</c> / <c>SpinButtonsGrid</c> / <c>Popup</c> 都是
/// code-behind 按名取的必要部件，重写模板时只要漏掉一个，在特定
/// <c>SpinButtonPlacementMode</c> 下就会空引用。而它的「内联加减号」外观正是原型
/// 需要的形状 —— 与其和它的模板契约缠斗，不如自己实现同一语义：
/// 取值区间、步长、输入校验、越界夹紧这四件事就是全部。
///
/// 【状态归属】
/// Value 是唯一的真相。文本只是它的投影：
/// 失焦 / 回车 / 点按步进时把文本解析回 Value，随后立刻用 Value 反向重写文本。
/// 这样「输入框里显示的数字」与「实际下发的数字」不可能分叉 —— 调 CO 时
/// 显示 -18 却下发 -17 是能烧 CPU 的那类事故。
/// </summary>
public sealed class NumberStepper : Control
{
    private const string PartInput = "InputBox";
    private const string PartUp = "UpButton";
    private const string PartDown = "DownButton";

    private TextBox? _input;
    private Button? _up;
    private Button? _down;
    private bool _projecting;

    public NumberStepper() => DefaultStyleKey = typeof(NumberStepper);

    // ==================== 依赖属性 ====================

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberStepper),
        new PropertyMetadata(0, OnValuePropertyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberStepper),
        new PropertyMetadata(int.MinValue, OnLimitPropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberStepper),
        new PropertyMetadata(int.MaxValue, OnLimitPropertyChanged));

    public static readonly DependencyProperty SmallChangeProperty = DependencyProperty.Register(
        nameof(SmallChange), typeof(int), typeof(NumberStepper),
        new PropertyMetadata(1));

    public static readonly DependencyProperty ShowPlusSignProperty = DependencyProperty.Register(
        nameof(ShowPlusSign), typeof(bool), typeof(NumberStepper),
        new PropertyMetadata(false, OnValuePropertyChanged));

    /// <summary>当前值。双向绑定目标。</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>点一次 ± 的步长。</summary>
    public int SmallChange
    {
        get => (int)GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    /// <summary>正数是否带 "+" 号。CO 偏移需要（+5 / -5 对称），频率不需要。</summary>
    public bool ShowPlusSign
    {
        get => (bool)GetValue(ShowPlusSignProperty);
        set => SetValue(ShowPlusSignProperty, value);
    }

    // ==================== 模板 ====================

    protected override void OnApplyTemplate()
    {
        Detach();

        base.OnApplyTemplate();

        _input = GetTemplateChild(PartInput) as TextBox;
        _up = GetTemplateChild(PartUp) as Button;
        _down = GetTemplateChild(PartDown) as Button;

        if (_up is not null)
            _up.Click += OnUpClick;
        if (_down is not null)
            _down.Click += OnDownClick;

        if (_input is not null)
        {
            _input.TextChanged += OnInputTextChanged;
            _input.LostFocus += OnInputLostFocus;
            _input.KeyDown += OnInputKeyDown;
        }

        UpdateStepperButtons();
        ProjectText();
    }

    private void Detach()
    {
        if (_up is not null)
            _up.Click -= OnUpClick;
        if (_down is not null)
            _down.Click -= OnDownClick;

        if (_input is not null)
        {
            _input.TextChanged -= OnInputTextChanged;
            _input.LostFocus -= OnInputLostFocus;
            _input.KeyDown -= OnInputKeyDown;
        }

        _input = null;
        _up = null;
        _down = null;
    }

    // ==================== 交互 ====================

    private void OnUpClick(object sender, RoutedEventArgs e) => Step(+1);

    private void OnDownClick(object sender, RoutedEventArgs e) => Step(-1);

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:
                Step(+1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down:
                Step(-1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                CommitText();
                e.Handled = true;
                break;
        }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        // 自己写文本时不回解析：ProjectText 里的赋值会再触发一次 TextChanged，
        // 不挡住就会在读值 → 写值 → 再读值之间来回打转。
        if (_projecting)
            return;
    }

    private void OnInputLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void Step(int direction)
    {
        int step = Math.Max(1, SmallChange);
        long next = (long)Value + ((long)direction * step);
        Value = (int)Math.Clamp(next, Minimum, Maximum);

        // 夹紧后值可能没变（已在边界），DP 回调不会触发，文本里的越界输入要手工回正。
        ProjectText();
    }

    private void CommitText()
    {
        if (_input is not null)
        {
            if (TryParse(_input.Text, out int parsed))
                Value = Math.Clamp(parsed, Minimum, Maximum);
        }

        // 解析失败、或输入被夹紧，都以 Value 为准回写文本 —— 不允许界面留着非法输入。
        ProjectText();
    }

    // ==================== 投影 ====================

    private static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 全角/排版用的减号（U+2212）在中文输入法里很容易被带进来。
        string raw = text.Trim().Replace('\u2212', '-').TrimStart('+');
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private void ProjectText()
    {
        if (_input is null)
            return;

        string text = Format(Value);
        if (string.Equals(_input.Text, text, StringComparison.Ordinal))
            return;

        _projecting = true;
        try
        {
            _input.Text = text;
        }
        finally
        {
            _projecting = false;
        }
    }

    private string Format(int value) =>
        ShowPlusSign && value > 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private void UpdateStepperButtons()
    {
        if (_up is not null)
            _up.IsEnabled = Value < Maximum;
        if (_down is not null)
            _down.IsEnabled = Value > Minimum;
    }

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        NumberStepper stepper = (NumberStepper)d;
        stepper.UpdateStepperButtons();
        stepper.ProjectText();
    }

    private static void OnLimitPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        NumberStepper stepper = (NumberStepper)d;
        stepper.UpdateStepperButtons();
    }
}
