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
    private readonly Action<CoreSlotViewModel> _apply;
    private bool _suppressDirty;

    public CoreSlotViewModel(int index, int coresPerCcd, int hardwareValue, Action<CoreSlotViewModel> apply)
    {
        Index = index;
        _apply = apply;

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

    partial void OnValueChanged(int value) => RaiseDirty();

    private void RaiseDirty()
    {
        if (_suppressDirty)
            return;

        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsDirty));
    }
}
