using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 调校页的一格 CO。一个物理核心一个实例。
///
/// 【为什么每格自带 ApplyCommand 而不是页面统一收集】
/// 「逐核」不只是展示逐核：写入失败时必须能定位到具体哪个核没写进去。
/// 让每格自己发起写入，失败信息天然带着核号，回滚也只回滚这一格。
/// </summary>
public sealed partial class CoreSlotViewModel : ObservableObject
{
    /// <summary>
    /// CO 值域下界。步进钳制与下发校验共用这一组常量 ——
    /// 两处各写一份迟早会漂移，漂移的结果是「界面允许写、下发时被拒」。
    /// </summary>
    public const int CoMin = -50;

    /// <summary>CO 值域上界。</summary>
    public const int CoMax = 50;

    private readonly Action<CoreSlotViewModel> _apply;
    private readonly Action<CoreSlotViewModel> _edited;
    private bool _suppressDirty;

    public CoreSlotViewModel(
        int index,
        int coresPerCcd,
        int hardwareValue,
        Action<CoreSlotViewModel> apply,
        Action<CoreSlotViewModel> edited)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(edited);

        Index = index;
        _apply = apply;
        _edited = edited;

        int perCcd = coresPerCcd > 0 ? coresPerCcd : 8;
        CcdText = $"CCD{index / perCcd}";
        IndexText = index.ToString("00");

        HardwareValue = hardwareValue;
        Value = hardwareValue == int.MinValue ? 0 : hardwareValue;

        ApplyCommand = new RelayCommand(() => _apply(this));
    }

    /// <summary>线性核序号（0 起）。也是回滚与日志里定位核心的唯一标识。</summary>
    public int Index { get; }

    /// <summary>两位序号，等宽显示用。</summary>
    public string IndexText { get; }

    public string CcdText { get; }

    /// <summary>最近一次从 SMU 读回的值；<see cref="int.MinValue"/> 表示该核读不到（熔断 / 不支持）。</summary>
    public int HardwareValue { get; private set; }

    public bool HardwareKnown => HardwareValue != int.MinValue;

    public string HardwareText => HardwareKnown ? UiConverters.ToSignedText(HardwareValue) : "—";

    /// <summary>界面编辑值。NumberStepper 双向绑这个。整数是因为 CO 本身就是整数寄存器值。</summary>
    [ObservableProperty]
    public partial int Value { get; set; }

    public string ValueText => UiConverters.ToSignedText(Value);

    /// <summary>
    /// 纯 TextBox 的字符串双向绑定入口。容错解析：空串与非法输入直接忽略，
    /// 不抛异常 —— 输入中间态（刚打出一个「-」）不能把绑定打崩。
    /// </summary>
    public string EditText
    {
        get => Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsed))
            {
                Value = parsed;
            }
        }
    }

    /// <summary>编辑值与硬件值不一致 —— 只有 dirty 的格子才值得下发。</summary>
    public bool IsDirty => !HardwareKnown || Value != HardwareValue;

    public IRelayCommand ApplyCommand { get; }

    /// <summary>
    /// 步进 +1。
    ///
    /// 【为什么不接参数】
    /// 前端把命令经 TextBox.Tag（减）/ Header（加）穿透进控件模板，
    /// 模板内按钮没有 CommandParameter（每格自带命令，入口即身份），所以这里不接参数。
    /// </summary>
    [RelayCommand]
    private void Increment() => Step(1);

    /// <summary>步进 -1。</summary>
    [RelayCommand]
    private void Decrement() => Step(-1);

    private void Step(int delta)
    {
        int next = Math.Clamp(Value + delta, CoMin, CoMax);
        if (next == Value)
            return;

        Value = next;

        // 步进按钮走命令，不经过 TextChanged：脏状态必须由这里显式上报，
        // 否则浮动保存条与方案名旁的 * 角标都不会出现。
        _edited(this);
    }

    public int RoundedValue => Value;

    /// <summary>回填硬件值。刷新/写入成功后调用，同时清掉 dirty 态。</summary>
    public void UpdateHardware(int hardwareValue)
    {
        HardwareValue = hardwareValue;
        OnPropertyChanged(nameof(HardwareValue));
        OnPropertyChanged(nameof(HardwareKnown));
        OnPropertyChanged(nameof(HardwareText));

        // 回填时把编辑值同步成硬件值：用户看到的就是「这台机器现在的样子」。
        _suppressDirty = true;
        try
        {
            Value = hardwareValue == int.MinValue ? 0 : hardwareValue;
        }
        finally
        {
            _suppressDirty = false;
        }

        RaiseDirty();
    }

    /// <summary>从配置文件加载的值（不写硬件）。</summary>
    public void LoadDraft(int draftValue)
    {
        if (draftValue == int.MinValue)
            return;

        Value = draftValue;
        RaiseDirty();
    }

    partial void OnValueChanged(int value)
    {
        // EditText 必须无条件通知：它绑在 TextBox 上，
        // 后端改 Value（回填硬件值 / 步进）后界面不跟着变，格子就一直是旧值。
        OnPropertyChanged(nameof(EditText));
        RaiseDirty();
    }

    private void RaiseDirty()
    {
        if (_suppressDirty)
            return;

        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsDirty));
    }
}
