using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;
using Zentwik.Core.Threading;

namespace Zentwik.App.ViewModels;

/// <summary>
/// 调校页。三条业务主线：CO 逐核读写、FMax 上限、电源策略（升频模式 / 核心停放）。
///
/// 【线程模型】
/// SMU 访问一律 <c>Task.Run</c> 丢到线程池：一次 <c>ReadSnapshot</c> 要逐核读寄存器，
/// 16 核大约数百毫秒，放 UI 线程就是肉眼可见的卡顿。命令由 UI 线程发起，
/// <c>await</c> 之后续体回到 UI 线程（宿主已挂 <c>SynchronizationContext</c>），
/// 所以回填集合不需要再手工封送。
///
/// 【写入语义】
/// 1. 下发前先读快照 —— 不只是为了回填，更是为了**回滚**：任何一处失败都要能把
///    硬件恢复到下发前，否则用户会停在一个「只写进去一半」的状态。
/// 2. 写入值逐核回读校验由 Core 完成（<c>SmuWriteResult.VerifyFailed</c>），
///    界面只负责把失败明细翻成人话。
/// 3. <c>int.MinValue</c> 是「跳过该核」的哨兵值，逐核写入正是靠它实现的：
///    只给目标核一个值，其余核全是哨兵，SMU 就只收到那一条。
/// </summary>
public sealed partial class TuningViewModel : ObservableObject
{
    /// <summary>CO 允许区间，与 Core 的下发校验保持一致（防砖红线）。</summary>
    private const int CoMin = -50;
    private const int CoMax = 50;

    private const int FMaxMin = 400;
    private const int FMaxMax = 5400;

    /// <summary>
    /// 滑杆停手多久之后自动提交电源策略（毫秒）。
    /// 取值与原 View 层计时器一致：拖动滑块时每一帧都会触发 ValueChanged，
    /// 而写一次电源策略要动多个注册表索引、再重新激活一次方案 ——
    /// 逐帧提交会让整机在拖动全程持续掉频。
    /// </summary>
    private const int PowerApplyDebounceMs = 700;

    /// <summary>
    /// Windows 电源计划的四个档位。刻意写死顺序而不用 <c>Enum.GetValues</c>：
    /// 后者在 Native AOT 下要靠运行时枚举元数据，写死一个数组既零反射也不会因为
    /// 有人往枚举中间插值而让界面选项顺序错位。
    /// </summary>
    private static readonly PowerPlanProfile[] PlanProfiles =
    {
        PowerPlanProfile.Unrestricted,
        PowerPlanProfile.Gentle,
        PowerPlanProfile.Balanced,
        PowerPlanProfile.Quiet,
    };

    private readonly ISmuReader _smu;
    private readonly ITuningStore _tuning;
    private readonly ProfileService _profiles;
    private readonly ILogService _log;
    private readonly IUiDispatcher _dispatcher;

    private int _coreCount;
    private uint _hardwareFMax;

    /// <summary>回填方案下拉时挡住 OnProfileIndexChanged，避免「刷新列表」被当成「用户切换方案」。</summary>
    private bool _suppressProfile;

    /// <summary>本轮读到的出厂 FMax 基线（内存态，随下一次落盘持久化）。</summary>
    private uint _fMaxBaseline;

    /// <summary>
    /// 初始化回填是否已结束。回填会把 ParkingValue 从默认值改写成硬件真实值，
    /// 那不是用户操作 —— 此时放开自动提交等于「一启动就写一次电源策略」。
    /// </summary>
    private bool _powerReady;

    /// <summary>
    /// 本机是否正在提交 / 回读电源策略。用于阻断自激环：
    /// 提交 → 回读 → ParkingValue 变化 → 滑块事件 → 再次提交 → …
    /// </summary>
    private bool _powerApplying;

    /// <summary>
    /// 防抖代次。每次滑杆变更递增，挂起中的提交据此判定是否作废：
    /// 只有最后一次（用户停手）能真正执行。
    /// </summary>
    private int _powerApplyGeneration;

    public TuningViewModel(ISmuReader smu, ITuningStore tuning, ProfileService profiles, ILogService log, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(smu);
        _smu = smu;
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        // 电源防抖的续体要回到 UI 线程才能改绑定属性，调度器是唯一的跨线程通道。
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;

        foreach ((ProcessorPowerTuning.BoostMode _, string label, string _) in ProcessorPowerTuning.BoostOptions)
            BoostOptions.Add(label);

        foreach (PowerPlanProfile profile in PlanProfiles)
            PowerPlanOptions.Add(ProcessorPowerPolicy.Label(profile));

        Cores.CollectionChanged += (_, _) => RaiseCoreSummary();
    }

    // ==================== 只读展示状态 ====================

    public ObservableCollection<CoreSlotViewModel> Cores { get; } = new();

    public ObservableCollection<string> BoostOptions { get; } = new();

    public ObservableCollection<string> PowerPlanOptions { get; } = new();

    /// <summary>已保存方案的名字列表，与 <see cref="ProfileService.Items"/> 一一对应。</summary>
    public ObservableCollection<string> ProfileOptions { get; } = new();

    /// <summary>当前选中的方案。切换即把该方案的 CO / FMax 填进界面（不碰硬件）。</summary>
    [ObservableProperty]
    public partial int ProfileIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string CpuName { get; set; } = "未探测";

    [ObservableProperty]
    public partial string CpuSubText { get; set; } = "等待同步 SMU";

    [ObservableProperty]
    public partial string TopologyText { get; set; } = "—";

    [ObservableProperty]
    public partial string CoreSummary { get; set; } = "0 核";

    /// <summary>是否已拿到核心列表。为 false 时展示空状态，而不是一块空白卡片。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCoreGridEmpty))]
    public partial bool HasCores { get; set; }

    public bool IsCoreGridEmpty => !HasCores;

    [ObservableProperty]
    public partial string SmuStateText { get; set; } = "未连接";

    [ObservableProperty]
    public partial bool SmuAvailable { get; set; }

    // ==================== 提示条 ====================

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial UiSeverity StatusLevel { get; set; } = UiSeverity.Info;

    // ==================== 忙碌遮罩 ====================

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPowerCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = "正在与 SMU 通信…";

    // ==================== FMax ====================

    [ObservableProperty]
    public partial int FMaxValue { get; set; } = FMaxMax;

    partial void OnFMaxValueChanged(int value) => OnPropertyChanged(nameof(FMaxText));

    /// <summary>纯 TextBox 的字符串双向绑定入口（容错解析，非法输入忽略）。</summary>
    public string FMaxText
    {
        get => FMaxValue.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsed) && parsed >= 0)
            {
                FMaxValue = parsed;
            }
        }
    }

    [ObservableProperty]
    public partial string FMaxHint { get; set; } = "未同步";

    public int FMaxMinimum => FMaxMin;

    public int FMaxMaximum => FMaxMax;

    /// <summary>
    /// FMax 读取通道是否受支持（契约 2.2 硬闸门）。
    /// 快照读取成功后按 <c>snapshot.FMaxMHz &gt; 0</c> 赋值；
    /// 读取失败保持上次结果不变 —— 一次通信抖动不该把 FMax 输入整个灰掉。
    /// </summary>
    [ObservableProperty]
    public partial bool IsFMaxSupported { get; set; }

    // ==================== 电源策略 ====================

    [ObservableProperty]
    public partial bool PowerAvailable { get; set; } = true;

    /// <summary>Windows 电源计划当前档位。-1 = 未检测到（系统未暴露该索引）。</summary>
    [ObservableProperty]
    public partial int PowerPlanIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string PowerPlanHint { get; set; } = "未读取";

    [ObservableProperty]
    public partial int BoostIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string BoostHint { get; set; } = "未读取";

    [ObservableProperty]
    public partial double ParkingValue { get; set; } = 100;

    /// <summary>
    /// 核心停放的整数值，专供数字步进器双向绑定。
    /// Slider 的 Value 是 double（拖动过程必然是小数），而步进器要整数；
    /// 两个属性做幂等同步：取整后再比较，一致就不再回写，避免互相触发成死循环。
    /// </summary>
    [ObservableProperty]
    public partial int ParkingInt { get; set; } = 100;

    partial void OnParkingValueChanged(double value)
    {
        int rounded = (int)Math.Round(value);
        if (ParkingInt != rounded)
            ParkingInt = rounded;
    }

    partial void OnParkingIntChanged(int value)
    {
        OnPropertyChanged(nameof(ParkingText));

        if (Math.Abs(ParkingValue - value) >= 0.5)
            ParkingValue = value;
    }

    /// <summary>核心停放 TextBox 的字符串双向绑定入口（容错解析，非法输入忽略）。</summary>
    public string ParkingText
    {
        get => ParkingInt.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsed) && parsed >= 0)
            {
                ParkingValue = Math.Min(parsed, 100);
            }
        }
    }

    [ObservableProperty]
    public partial string ParkingHint { get; set; } = "未读取";

    /// <summary>
    /// 核心停放是否受支持（契约 2.3 硬闸门）。
    /// 电源策略读取成功后按 <c>TryReadCoreParking</c> 的 parkOk 赋值；
    /// <see cref="PowerAvailable"/> 为 false 时必为 false。
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoreParkingSupported { get; set; }

    // ==================== 脏数据状态（浮动保存条 / 预设星号联动） ====================

    /// <summary>草稿与基准不一致。驱动底部浮动保存条与预设下拉框右上角的 * 角标。</summary>
    [ObservableProperty]
    public partial bool IsDraftDirty { get; set; }

    /// <summary>页面在用户输入事件里调用；初始化回填不会走到这里。</summary>
    public void MarkDirty() => IsDraftDirty = true;

    /// <summary>放弃：界面回到当前选中方案的 CO / FMax 值（不碰硬件）。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void DiscardDraft()
    {
        int index = ProfileIndex;
        if (index >= 0 && index < _profiles.Items.Count)
        {
            ProfileSnapshot snapshot = _profiles.Items[index];
            int[] margins = snapshot.CurveOptimizer ?? Array.Empty<int>();

            for (int i = 0; i < Cores.Count; i++)
                Cores[i].LoadDraft(i < margins.Length ? margins[i] : Cores[i].HardwareValue);

            if (snapshot.FMaxMHz > 0)
                FMaxValue = (int)snapshot.FMaxMHz;

            RaiseCoreSummary();
        }

        IsDraftDirty = false;
    }

    /// <summary>另存为：把当前草稿（CO + FMax）存成一份新方案并选中新方案。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void SaveDraftAs()
    {
        ProfileSnapshot snapshot = new()
        {
            Name = $"自定义方案 {DateTime.Now:HHmmss}",
            CurveOptimizer = Cores.Select(c => c.Value).ToArray(),
            FMaxMHz = (uint)Math.Max(0, FMaxValue),
        };

        _profiles.Save(snapshot);
        ReloadProfiles();

        // ReloadProfiles 把索引压回 0，这里显式指向刚存进去的那份。
        ProfileIndex = _profiles.Items.Count - 1;
        IsDraftDirty = false;

        SetStatus(UiSeverity.Success, "已另存", $"当前草稿已保存为方案「{snapshot.Name}」。");
    }

    // ==================== 命令 ====================

    private bool CanRun() => !IsBusy;

    /// <summary>刷新：从 SMU 重读全部状态并回填界面（含已下发值与拓扑）。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        if (_coreCount == 0 && !_smu.IsAvailable)
        {
            // 驱动没就绪时不要一味读：ReadSnapshot 内部有 2 秒冷却，连点会白等。
            SetStatus(UiSeverity.Warning, "SMU 不可用",
                _smu.LastError ?? "PawnIO 驱动未加载或权限不足，请以管理员身份运行并确认驱动已安装。");
            return;
        }

        SetBusy(true, "正在读取 SMU 状态…");
        try
        {
            SmuSnapshot snapshot = await Task.Run(() => _smu.ReadSnapshot());
            ApplySnapshot(snapshot);

            SetStatus(snapshot.Success ? UiSeverity.Success : UiSeverity.Warning,
                snapshot.Success ? "已同步" : "同步不完整",
                snapshot.Success
                    ? $"已读取 {snapshot.CoreMargins.Length} 核 CO 与 FMax {snapshot.FMaxMHz} MHz。"
                    : (snapshot.Error ?? "SMU 探测失败。"));
        }
        catch (Exception ex)
        {
            _log.Error($"调校页刷新失败：{ex.Message}");
            SetStatus(UiSeverity.Error, "刷新失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>加载：把 tuning.json 里的已保存设定填进界面（不写硬件）。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Load()
    {
        _tuning.Load();
        TuningProfile profile = _tuning.Profile;

        int[] margins = profile.CurveOptimizer ?? Array.Empty<int>();
        if (margins.Length == 0 && profile.FMaxMHz == 0)
        {
            SetStatus(UiSeverity.Info, "没有已保存的调校", "tuning.json 里没有 CO / FMax 记录。");
            return;
        }

        for (int i = 0; i < Cores.Count; i++)
            Cores[i].LoadDraft(i < margins.Length ? margins[i] : int.MinValue);

        if (profile.FMaxMHz > 0)
            FMaxValue = (int)profile.FMaxMHz;

        RaiseCoreSummary();
        SetStatus(UiSeverity.Info, "已加载配置",
            $"CO {margins.Length} 核，FMax {(profile.FMaxMHz == 0 ? "未设置" : profile.FMaxMHz + " MHz")}" +
            (string.IsNullOrEmpty(profile.AppliedAt) ? "。" : $"，上次应用 {profile.AppliedAt}。"));
    }

    /// <summary>保存：只落盘，不下发。保存的是「期望状态」，下次启动由静默模式应用。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Save()
    {
        if (Cores.Count == 0)
        {
            SetStatus(UiSeverity.Warning, "无可保存内容", "尚未读取到核心列表，请先刷新。");
            return;
        }

        TuningProfile profile = new()
        {
            CurveOptimizer = Cores.Select(c => c.RoundedValue).ToArray(),
            FMaxMHz = (uint)Math.Max(0, FMaxValue),
            FMaxBaselineMHz = BaselineFMax
        };

        _tuning.Save(profile);
        IsDraftDirty = false;
        SetStatus(UiSeverity.Success, "已保存", $"已写入 tuning.json（CO {profile.CurveOptimizer.Length} 核，FMax {profile.FMaxMHz} MHz）。");
    }

    /// <summary>
    /// 应用：校验 → 读快照 → 逐核下发 → 回读校验 → 失败整体回滚 → 成功落盘。
    /// 全程遮罩，禁止用户在写入过程中改动输入（改到一半的值会被下一次写入带上）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyAsync()
    {
        if (_coreCount <= 0 || Cores.Count == 0)
        {
            SetStatus(UiSeverity.Warning, "未就绪", "尚未读到 CPU 拓扑，请先刷新。");
            return;
        }

        foreach (CoreSlotViewModel slot in Cores)
        {
            if (slot.RoundedValue < CoMin || slot.RoundedValue > CoMax)
            {
                SetStatus(UiSeverity.Error, "CO 越界",
                    $"核心 {slot.Index}：{slot.RoundedValue}，允许区间 {CoMin} ~ {CoMax}。");
                return;
            }
        }

        SetBusy(true, "正在下发调校并回读校验…");
        try
        {
            // ---------- 1. 下发前快照（回滚基线） ----------
            SmuSnapshot before = await Task.Run(() => _smu.ReadSnapshot());
            bool canRollback = before.Success && before.CoreMargins.Length > 0;

            int[] target = new int[_coreCount];
            for (int i = 0; i < _coreCount; i++)
                target[i] = i < Cores.Count ? Cores[i].RoundedValue : 0;

            _log.Info($"调校应用：下发 CO {target.Length} 核");

            // ---------- 2. CO 下发 ----------
            SmuWriteResult co = await Task.Run(() => _smu.WriteCurveOptimizer(target));
            if (!co.AllSucceeded)
            {
                string detail = DescribeCoFailure(co);
                if (canRollback)
                {
                    int[] restore = BuildRestoreArray(before.CoreMargins);
                    SmuWriteResult rb = await Task.Run(() => _smu.WriteCurveOptimizer(restore));
                    detail += rb.AllSucceeded
                        ? " 已回滚至下发前状态。"
                        : " 回滚未完全成功，请立即刷新查看硬件实际值。";
                }
                else
                {
                    detail += " 快照不可用，未能回滚 —— 请刷新确认硬件实际值。";
                }

                _log.Error($"调校应用失败：{detail}");
                SetStatus(UiSeverity.Error, "CO 下发失败", detail);
                await RefreshQuietAsync();
                return;
            }

            foreach (CoreSlotViewModel slot in Cores)
            {
                if (co.Applied.TryGetValue(slot.Index, out int applied))
                    slot.UpdateHardware(applied);
            }

            // ---------- 3. FMax 下发（只在用户改动时写，避免无意义的寄存器写入） ----------
            uint requestedFMax = (uint)Math.Max(0, FMaxValue);
            bool fMaxApplied = false;
            if (requestedFMax > 0 && requestedFMax != _hardwareFMax)
            {
                SmuFMaxResult fm = await Task.Run(() => _smu.WriteFMax(requestedFMax));
                if (!fm.AllSucceeded)
                {
                    string detail = fm.Fatal is not null
                        ? $"FMax 写入失败：{fm.Fatal}"
                        : $"FMax 回读不一致（请求 {fm.Requested}，回读 {fm.ReadBack}）。";

                    if (_hardwareFMax > 0)
                    {
                        SmuFMaxResult rb = await Task.Run(() => _smu.WriteFMax(_hardwareFMax));
                        detail += rb.AllSucceeded
                            ? $" 已还原为 {_hardwareFMax} MHz。"
                            : " 还原失败，请手动刷新确认。";
                    }

                    _log.Error($"调校应用失败：{detail}");
                    SetStatus(UiSeverity.Error, "FMax 下发失败", detail);
                    await RefreshQuietAsync();
                    return;
                }

                fMaxApplied = true;
                _hardwareFMax = fm.ReadBack;
                FMaxValue = (int)fm.ReadBack;
                FMaxHint = $"{fm.ReadBack} MHz（回读一致）";
            }

            // ---------- 4. 成功：落盘 ----------
            TuningProfile profile = new()
            {
                CurveOptimizer = target,
                FMaxMHz = fMaxApplied ? (uint)Math.Max(0, FMaxValue) : _tuning.Profile.FMaxMHz,
                FMaxBaselineMHz = BaselineFMax
            };
            _tuning.Save(profile);

            RaiseCoreSummary();
            IsDraftDirty = false;
            SetStatus(UiSeverity.Success, "已应用",
                $"CO {co.Applied.Count} 核写入并回读一致" +
                (fMaxApplied ? $"，FMax {_hardwareFMax} MHz。" : "。"));
        }
        catch (Exception ex)
        {
            _log.Error($"调校应用异常：{ex}");
            SetStatus(UiSeverity.Error, "应用异常", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>单核下发。失败只回滚这一核 —— 整片回滚会把别的核也一起拽回旧值。</summary>
    private async Task ApplyCoreAsync(CoreSlotViewModel slot)
    {
        if (IsBusy)
            return;

        int value = slot.RoundedValue;
        if (value < CoMin || value > CoMax)
        {
            SetStatus(UiSeverity.Error, "CO 越界", $"核心 {slot.Index}：{value}，允许区间 {CoMin} ~ {CoMax}。");
            return;
        }

        if (slot.HardwareKnown && value == slot.HardwareValue)
        {
            SetStatus(UiSeverity.Info, "无需下发", $"核心 {slot.Index} 的目标值与硬件一致。");
            return;
        }

        if (_coreCount <= 0)
        {
            SetStatus(UiSeverity.Warning, "未就绪", "尚未读到 CPU 拓扑，请先刷新。");
            return;
        }

        SetBusy(true, $"正在写入核心 {slot.Index}…");
        try
        {
            // 哨兵值 = 其余核全跳过，SMU 只收到目标核这一条。
            int[] payload = new int[_coreCount];
            Array.Fill(payload, int.MinValue);
            payload[slot.Index] = value;

            SmuWriteResult result = await Task.Run(() => _smu.WriteCurveOptimizer(payload));

            if (result.Applied.TryGetValue(slot.Index, out int applied))
            {
                slot.UpdateHardware(applied);
                SetStatus(UiSeverity.Success, "已写入", $"核心 {slot.Index} CO {UiConverters.ToSignedText(applied)}，回读一致。");
                return;
            }

            // ---------- 失败：把这一核还原成下发前的硬件值 ----------
            if (slot.HardwareKnown)
            {
                int[] restore = new int[_coreCount];
                Array.Fill(restore, int.MinValue);
                restore[slot.Index] = slot.HardwareValue;

                SmuWriteResult rb = await Task.Run(() => _smu.WriteCurveOptimizer(restore));
                SetStatus(UiSeverity.Error, "核心写入失败",
                    $"{DescribeCoFailure(result)} 已尝试还原为 {UiConverters.ToSignedText(slot.HardwareValue)}" +
                    (rb.AllSucceeded ? "。" : "，还原失败，请刷新确认。"));
            }
            else
            {
                SetStatus(UiSeverity.Error, "核心写入失败", DescribeCoFailure(result));
            }

            _log.Error($"核心 {slot.Index} 写入失败：{DescribeCoFailure(result)}");
        }
        catch (Exception ex)
        {
            _log.Error($"核心 {slot.Index} 写入异常：{ex.Message}");
            SetStatus(UiSeverity.Error, "写入异常", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 应用电源策略：升频模式 + 核心停放。两者独立，各自校验并单独报告。
    ///
    /// 【自激环守卫】
    /// 提交会回读硬件，回读会改写 ParkingValue，滑块随之触发变更事件 ——
    /// 没有 <see cref="_powerApplying"/> 这道守卫就会「提交 → 回读 → 再提交」无限循环。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyPowerAsync()
    {
        if (_powerApplying)
            return;

        if (!PowerAvailable)
        {
            SetStatus(UiSeverity.Warning, "当前方案不支持", "当前电源方案没有升频模式 / 核心停放索引。");
            return;
        }

        // 本次提交作废此前挂起的防抖提交：用户已经通过下拉或滑杆确认了新的意图。
        _powerApplyGeneration++;

        SetBusy(true, "正在写入电源策略…");
        _powerApplying = true;
        try
        {
            List<string> done = new();
            List<string> failed = new();

            // 顺序是硬约束：电源计划是「整套索引」的批量写，它会覆盖升频模式与核心停放，
            // 所以必须先落计划，再落这两项更具体的设置，否则用户改的那两项会被计划冲掉。
            if (PowerPlanIndex >= 0 && PowerPlanIndex < PlanProfiles.Length)
            {
                PowerPlanProfile target = PlanProfiles[PowerPlanIndex];
                PowerPlanResult planResult = await Task.Run(() => ProcessorPowerPolicy.Apply(target));
                (planResult.Ok ? done : failed).Add(planResult.Ok
                    ? $"电源计划：{ProcessorPowerPolicy.Label(target)}"
                    : $"电源计划：{planResult.Error}");
            }

            if (BoostIndex >= 0 && BoostIndex < ProcessorPowerTuning.BoostOptions.Length)
            {
                ProcessorPowerTuning.BoostMode mode = ProcessorPowerTuning.BoostOptions[BoostIndex].Mode;
                PowerPlanResult result = await Task.Run(() => ProcessorPowerTuning.WriteBoost(mode));
                (result.Ok ? done : failed).Add(result.Ok ? $"升频模式：{ProcessorPowerTuning.BoostOptions[BoostIndex].Label}" : $"升频模式：{result.Error}");
            }

            uint parking = (uint)Math.Clamp(Math.Round(ParkingValue), 0, 100);
            PowerPlanResult parkResult = await Task.Run(() => ProcessorPowerTuning.WriteCoreParking(parking));
            (parkResult.Ok ? done : failed).Add(parkResult.Ok ? $"核心停放：{parking}%" : $"核心停放：{parkResult.Error}");

            await ReadPowerAsync();

            if (failed.Count == 0)
                SetStatus(UiSeverity.Success, "电源策略已应用", string.Join("；", done) + "。");
            else
                SetStatus(UiSeverity.Error, "电源策略部分失败", string.Join("；", failed) + "。");
        }
        catch (Exception ex)
        {
            _log.Error($"电源策略写入异常：{ex.Message}");
            SetStatus(UiSeverity.Error, "电源策略异常", ex.Message);
        }
        finally
        {
            _powerApplying = false;
            SetBusy(false);
        }
    }

    /// <summary>
    /// 停放滑杆变更：停手 <see cref="PowerApplyDebounceMs"/> 毫秒后自动提交电源策略。
    ///
    /// 【为什么防抖收敛在 ViewModel】
    /// 防抖与「谁来提交」必须同层：View 只负责把「用户改动了滑杆」这个事实报上来，
    /// 计时、作废、代次判定全由 ViewModel 负责。View 与 ViewModel 各持一套计时器时，
    /// 两边的计时器会互相触发对方的提交路径，代次判定也就无从谈起。
    ///
    /// 下拉框换项走立即提交（<see cref="ApplyPowerCommand"/>），不经过这条路径 ——
    /// 下拉是一次性选择，没有「拖到一半」的中间态，不需要防抖。
    /// </summary>
    public void SchedulePowerApply()
    {
        // 回填期间与本机提交期间都不接受自动提交：前者不是用户操作，后者会自激。
        if (!_powerReady || _powerApplying)
            return;

        // 代次递增：先前挂起的提交立即作废，只有最后一次（用户停手）能真正执行。
        int generation = ++_powerApplyGeneration;
        _ = DebouncedApplyAsync(generation);
    }

    private async Task DebouncedApplyAsync(int generation)
    {
        await Task.Delay(PowerApplyDebounceMs);

        // 代次已变 = 期间又有新变更，这次挂起的提交作废。
        if (generation != _powerApplyGeneration)
            return;

        // 统一回 UI 线程再改绑定属性：这条路径允许从任意线程进入。
        if (_dispatcher.IsCurrentThread)
            await ApplyPowerAsync();
        else
            _dispatcher.Post(() => _ = ApplyPowerAsync());
    }

    // ==================== 内部 ====================

    /// <summary>页面首次进入：先读硬件，再读电源策略。异常不外抛 —— 页面是 async void 调它。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            ReloadProfiles();
            await RefreshAsync();
            await ReadPowerAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"调校页初始化失败：{ex.Message}");
            SetStatus(UiSeverity.Error, "初始化失败", ex.Message);
        }
        finally
        {
            // 回填结束才放开自动提交：回填本身会改写 ParkingValue，
            // 那不是用户操作，放行等于启动即写一次电源策略。
            _powerReady = true;
        }
    }

    /// <summary>后台重读一次并回填，用于失败后让界面尽快回到真实状态（不弹提示条）。</summary>
    /// <summary>
    /// 重建方案下拉。名字列表与 ProfileService.Items 一一对应，索引即身份，
    /// 这样下拉框只需要 int 的 SelectedIndex，不必给 ComboBox 塞对象与转换器。
    /// </summary>
    private void ReloadProfiles()
    {
        _profiles.Load();

        _suppressProfile = true;
        try
        {
            ProfileOptions.Clear();
            foreach (ProfileSnapshot snapshot in _profiles.Items)
                ProfileOptions.Add(snapshot.Name);

            if (ProfileOptions.Count == 0)
            {
                // 一个方案都没有时也要有可选内容，否则下拉框是一块空壳。
                ProfileOptions.Add("自定义方案");
                ProfileIndex = 0;
            }
            else
            {
                ProfileIndex = 0;
            }
        }
        finally
        {
            _suppressProfile = false;
        }
    }

    /// <summary>
    /// 切换方案 = 把该方案的 CO / FMax 填进界面草稿，不写硬件。
    /// 写硬件必须由「应用」显式触发 —— 单纯看一眼方案就改寄存器，
    /// 是最容易把一台稳定机器搞崩的交互。
    /// </summary>
    partial void OnProfileIndexChanged(int value)
    {
        if (_suppressProfile)
            return;

        if (value < 0 || value >= _profiles.Items.Count)
            return;

        ProfileSnapshot snapshot = _profiles.Items[value];
        int[] margins = snapshot.CurveOptimizer ?? Array.Empty<int>();

        for (int i = 0; i < Cores.Count; i++)
            Cores[i].LoadDraft(i < margins.Length ? margins[i] : int.MinValue);

        if (snapshot.FMaxMHz > 0)
            FMaxValue = (int)snapshot.FMaxMHz;

        // 切方案 = 换了一份基准草稿，此前的「未保存修改」不再成立。
        IsDraftDirty = false;

        RaiseCoreSummary();
    }

    private async Task RefreshQuietAsync()
    {
        try
        {
            SmuSnapshot snapshot = await Task.Run(() => _smu.ReadSnapshot());
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn($"调校页静默刷新失败：{ex.Message}");
        }
    }

    private async Task ReadPowerAsync()
    {
        (bool planOk, PowerPlanProfile? plan, string planError) = await Task.Run(() =>
        {
            bool ok = ProcessorPowerPolicy.TryDetect(out PowerPlanProfile? detected, out string err);
            return (ok, detected, err);
        });

        // -1 表示「系统没暴露这个索引」，界面上表现为下拉框停在未选中态，
        // 而不是假装选中了均衡档 —— 假选中会让用户以为点「应用」能改到东西。
        PowerPlanIndex = planOk && plan.HasValue ? Array.IndexOf(PlanProfiles, plan.Value) : -1;
        PowerPlanHint = planOk && plan.HasValue ? ProcessorPowerPolicy.Label(plan.Value) : planError;

        (bool boostOk, uint boostRaw, string boostError, bool parkOk, uint parkPercent, string parkError) = await Task.Run(() =>
        {
            bool bOk = ProcessorPowerTuning.TryReadBoost(out uint bValue, out string bErr);
            bool pOk = ProcessorPowerTuning.TryReadCoreParking(out uint pValue, out string pErr);
            return (bOk, bValue, bErr, pOk, pValue, pErr);
        });

        if (!boostOk && !parkOk)
        {
            PowerAvailable = false;
            // 契约 2.3：整体不可用时停放闸门必为 false。
            IsCoreParkingSupported = false;
            BoostHint = boostError;
            ParkingHint = parkError;
            return;
        }

        PowerAvailable = true;

        // 硬闸门：升频可用但停放索引不存在时，停放那一行必须被前端禁用 / 隐藏。
        // parkOk 为 false 是「读到了不支持这个索引」这一确定结论，与读取异常不同：
        // 读取异常会在到达这一行之前抛出，属性自然保持上次的值不变。
        IsCoreParkingSupported = parkOk;

        if (boostOk)
        {
            int index = Array.FindIndex(ProcessorPowerTuning.BoostOptions,
                o => (uint)o.Mode == boostRaw);
            BoostIndex = index;
            BoostHint = index >= 0
                ? ProcessorPowerTuning.BoostOptions[index].Hint
                : "系统默认（未显式设置升频模式）";
        }
        else
        {
            BoostHint = boostError;
        }

        if (parkOk)
        {
            ParkingValue = parkPercent;
            ParkingHint = ProcessorPowerTuning.ParkingHint(parkPercent);
        }
        else
        {
            ParkingHint = parkError;
        }
    }

    private void ApplySnapshot(SmuSnapshot snapshot)
    {
        if (snapshot is null)
            return;

        SmuAvailable = snapshot.Success;
        SmuStateText = snapshot.Success ? "SMU 就绪" : (snapshot.Error ?? "SMU 不可用");

        // 硬闸门只在快照读成功时更新：读取失败保持上次结果不变。
        if (snapshot.Success)
            IsFMaxSupported = snapshot.FMaxMHz > 0;

        if (!string.IsNullOrWhiteSpace(snapshot.CpuName))
            CpuName = snapshot.CpuName;

        CpuSubText = string.IsNullOrWhiteSpace(snapshot.CodeName)
            ? "SMU 未响应"
            : $"{snapshot.CodeName} · SMU {snapshot.SmuType} {snapshot.SmuVersion}";

        if (snapshot.Ccds > 0 && snapshot.CoresPerCcd > 0)
        {
            TopologyText = $"{snapshot.Ccds} CCD × {snapshot.CoresPerCcd} 核 · " +
                           $"{snapshot.PhysicalCores} 物理 / {snapshot.Cores} 线程";
        }

        if (snapshot.CoreMargins.Length > 0)
        {
            _coreCount = snapshot.CoreMargins.Length;
            EnsureCoreSlots(snapshot.CoreMargins, (int)Math.Max(1, snapshot.CoresPerCcd));
        }

        if (snapshot.FMaxMHz > 0)
        {
            _hardwareFMax = snapshot.FMaxMHz;
            FMaxValue = (int)snapshot.FMaxMHz;
            FMaxHint = $"{snapshot.FMaxMHz} MHz（硬件当前值）";

            // 首次读到的 FMax 就是出厂基线：全局重置要回到它。
            // 只记在内存里，随下一次「保存 / 应用」一起落盘 —— TuningProfile.Save 会
            // 无条件刷新 AppliedAt，为了记一个基线去写盘，会让「上次应用时间」变成谎话。
            if (_fMaxBaseline == 0)
                _fMaxBaseline = snapshot.FMaxMHz;
        }
        else
        {
            FMaxHint = "该平台未提供 FMax 读取通道";
        }
    }

    /// <summary>
    /// 重建核格子。数量不变时只回填硬件值（避免整块重排导致滚动位置跳动）。
    /// </summary>
    private void EnsureCoreSlots(int[] margins, int coresPerCcd)
    {
        if (Cores.Count == margins.Length)
        {
            for (int i = 0; i < Cores.Count; i++)
                Cores[i].UpdateHardware(margins[i]);
            RaiseCoreSummary();
            return;
        }

        foreach (CoreSlotViewModel old in Cores)
            old.PropertyChanged -= OnSlotPropertyChanged;

        Cores.Clear();
        for (int i = 0; i < margins.Length; i++)
        {
            CoreSlotViewModel slot = new(i, coresPerCcd, margins[i], s => _ = ApplyCoreAsync(s));
            slot.PropertyChanged += OnSlotPropertyChanged;
            Cores.Add(slot);
        }

        RaiseCoreSummary();
    }

    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CoreSlotViewModel.IsDirty) or nameof(CoreSlotViewModel.Value))
            RaiseCoreSummary();
    }

    /// <summary>出厂基线优先级：磁盘上已记的 &gt; 本轮读到的 &gt; 当前硬件值。</summary>
    private uint BaselineFMax
    {
        get
        {
            uint stored = _tuning.Profile.FMaxBaselineMHz;
            if (stored != 0)
                return stored;
            return _fMaxBaseline != 0 ? _fMaxBaseline : _hardwareFMax;
        }
    }

    private void RaiseCoreSummary()
    {
        HasCores = Cores.Count > 0;

        int dirty = Cores.Count(c => c.IsDirty);
        CoreSummary = dirty == 0
            ? $"{Cores.Count} 核 · 与硬件一致"
            : $"{Cores.Count} 核 · {dirty} 核待下发";
    }

    private int[] BuildRestoreArray(int[] margins)
    {
        int[] restore = new int[_coreCount];
        Array.Fill(restore, int.MinValue);
        for (int i = 0; i < restore.Length && i < margins.Length; i++)
            restore[i] = margins[i];
        return restore;
    }

    private static string DescribeCoFailure(SmuWriteResult result)
    {
        List<string> parts = new();
        if (!string.IsNullOrEmpty(result.Fatal))
            parts.Add($"致命错误：{result.Fatal}");
        if (result.WriteFailed.Count > 0)
            parts.Add($"被拒核心：{string.Join(", ", result.WriteFailed)}");
        if (result.VerifyFailed.Count > 0)
            parts.Add($"回读不一致核心：{string.Join(", ", result.VerifyFailed)}");
        return parts.Count == 0 ? "未知失败。" : string.Join("；", parts) + "。";
    }

    private void SetBusy(bool busy, string? text = null)
    {
        IsBusy = busy;
        if (!string.IsNullOrEmpty(text))
            BusyText = text;
    }

    private void SetStatus(UiSeverity level, string title, string message)
    {
        StatusLevel = level;
        StatusTitle = title;
        StatusMessage = message;
        // 原型里的提示条是「回滚告警」，只在出错时出现；成功/信息不占用首屏。
        IsStatusVisible = level is UiSeverity.Error or UiSeverity.Warning;
    }
}
