using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZenForge.Helpers;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>单个成员进程（用于实际下发绑核）。</summary>
    public sealed class ProcessMember
    {
        public int Pid;
        public string Path = string.Empty;

        /// <summary>PROCESSENTRY32.pcPriClassBase 的原始值，只用于展示。</summary>
        public int PriorityClass;
    }

    /// <summary>进程列表中的一组（按进程名合并后的行）。</summary>
    public sealed class ProcessGroupRow : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;

        private int _count;
        public int Count
        {
            get => _count;
            set
            {
                if (_count == value) return;
                _count = value;
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(DisplayName));   // 显示名随实例数变化
            }
        }

        /// <summary>该名称下所有实例的平均 CPU 占用率（数值，用于排序）。</summary>
        private double _cpuValue;
        public double CpuValue
        {
            get => _cpuValue;
            set
            {
                if (_cpuValue == value) return;
                _cpuValue = value;
                OnPropertyChanged(nameof(CpuValue));
                OnPropertyChanged(nameof(CpuText));        // CpuText 由 CpuValue 派生
            }
        }

        /// <summary>平均 CPU 占用率文本。</summary>
        public string CpuText => CpuValue.ToString("F1") + "%";

        private string _priorityText = "—";

        /// <summary>
        /// 进程优先级。
        ///
        /// 【为什么从只读改成可改】
        /// 早期注释担心「把忙等型进程提到 High 会把桌面饿死」。这个担心只对
        /// RealTime 成立 —— 它高于系统输入线程，一旦某个忙等进程拿到 RealTime，
        /// 鼠标键盘会直接卡住。High 只是「优先于普通进程」，任务管理器本来就能改，
        /// 我们拦着不给改，用户只会去任务管理器绕一圈，顺带绕开我们所有的说明。
        ///
        /// 下拉里刻意不含 RealTime，理由见 <see cref="ProcessBindingPage.PriorityOptions"/>。
        ///
        /// 【保留 PriorityText 的原因】
        /// 系统返回的优先级值不一定落在四个标准档上（有的进程是 13 = High
        /// 的变体，有的驱动进程是 24）。下拉的 SelectedItem 绑不上时会显示空白，
        /// 那看起来像"这个进程没有优先级"。PriorityText 保留原始标签，
        /// 由 SelectedItem 绑不上时的兜底显示承担解释职责。
        /// </summary>
        public string PriorityText
        {
            get => _priorityText;
            set
            {
                if (_priorityText == value)
                    return;
                _priorityText = value;
                OnPropertyChanged(nameof(PriorityText));
            }
        }

        private string _prioritySelection = ProcessBindingPage.PriorityNormal;

        /// <summary>
        /// 优先级下拉的选中项。
        ///
        /// 选中即下发（不需要「应用」）：优先级只作用于进程的当前实例，
        /// 不落盘、不写规则，进程退出后自然回到默认，不存在"只改了一半"的状态。
        /// 这正是它和掩码下拉的区别 —— 掩码要落盘，所以必须显式确认。
        /// </summary>
        public string PrioritySelection
        {
            get => _prioritySelection;
            set
            {
                if (value == _prioritySelection || string.IsNullOrEmpty(value))
                    return;

                _prioritySelection = value;
                OnPropertyChanged(nameof(PrioritySelection));

                // 初始回填阶段不触发下发（与 Selection 的 _suppress 同一套路）
                if (!_suppressPriority)
                    PriorityChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>回填优先级时不触发下发。两条下拉共用 <see cref="_suppress"/>。</summary>
        private bool _suppressPriority;

        /// <summary>优先级下拉的悬停说明：把当前档位的实际含义写清楚。</summary>
        public string PriorityHint => _prioritySelection switch
        {
            ProcessBindingPage.PriorityHigh =>
                "High：优先于普通进程获得 CPU 时间。适合游戏、实时音频等对延迟敏感的程序。",
            ProcessBindingPage.PriorityAboveNormal =>
                "Above Normal：比普通略高，但仍会让位于 High。适合前台工作程序。",
            ProcessBindingPage.PriorityBelowNormal =>
                "Below Normal：让位于普通进程，适合后台同步、下载。",
            ProcessBindingPage.PriorityLow =>
                "Low：只在系统空闲时获得 CPU 时间。适合压缩、索引等纯后台任务。",
            _ => "Normal：系统默认优先级。",
        };

        /// <summary>用户改了优先级（参数是新的档位标签）。</summary>
        public event EventHandler? PriorityChanged;

        /// <summary>回填优先级（不触发下发）。</summary>
        public void SetInitialPriority(string label)
        {
            _suppressPriority = true;
            try
            {
                _prioritySelection = label;
                OnPropertyChanged(nameof(PrioritySelection));
                OnPropertyChanged(nameof(PriorityHint));
            }
            finally
            {
                _suppressPriority = false;
            }
        }

        /// <summary>该名称下的全部进程实例（PID + 路径）。</summary>
        public List<ProcessMember> Members { get; set; } = new();
        /// <summary>该进程名下的可执行文件路径（取首个实例，同名多实例路径相同）。</summary>
        private string _path = string.Empty;
        public string Path
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value;
                OnPropertyChanged(nameof(Path));
            }
        }

        /// <summary>是否允许选择绑定目标（CPU Sets 拓扑不可用时为 false → 下拉框置灰）。</summary>
        private bool _canBind = true;
        public bool CanBind
        {
            get => _canBind;
            set
            {
                if (_canBind == value) return;
                _canBind = value;
                OnPropertyChanged(nameof(CanBind));
            }
        }

        /// <summary>列表显示名：msedge.exe (12个进程)。</summary>
        public string DisplayName => Count <= 1 ? Name : $"{Name} ({Count}个进程)";

        private bool _flash;

        /// <summary>绑定指令下发后的瞬时高亮（XAML 中驱动整行闪青色）。</summary>
        public bool Flash
        {
            get => _flash;
            set
            {
                if (_flash == value)
                    return;
                _flash = value;
                OnPropertyChanged(nameof(Flash));
            }
        }

        private string _selection = "不绑定";
        private bool _suppress;

        /// <summary>下拉框选中项（不绑定 / CCD0 / CCD1 / 掩码名）。</summary>
        public string Selection
        {
            get => _selection;
            set
            {
                if (value == _selection)
                    return;
                _selection = value;
                // 仅当用户主动选择时才触发绑定；初始回填时由 SetInitialSelection 抑制
                if (!_suppress)
                    TargetChanged?.Invoke(this, EventArgs.Empty);
                OnPropertyChanged(nameof(Selection));
            }
        }

        /// <summary>初始回填已保存规则（不触发绑定）。</summary>
        public void SetInitialSelection(string value)
        {
            _suppress = true;
            _selection = value;
            _suppress = false;
            OnPropertyChanged(nameof(Selection));
        }

        public event EventHandler? TargetChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>已保存规则列表的一行（进程名 + 掩码 + 启用状态）。</summary>
    public sealed class SavedRuleRow : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        /// <summary>该规则对应的可执行文件路径（首次记录时从运行实例取得）。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>该规则当前绑定的掩码 Id（仅用于展示与日志）。</summary>
        public string MaskId { get; set; } = string.Empty;

        /// <summary>
        /// 行首下拉框的选中项（不绑定 / CCD0 / CCD1 / 掩码名）。
        /// 与上方「运行中进程」的下拉框共用同一份 <see cref="ProcessBindingPage.TargetOptions"/>。
        /// 用户在规则行改选掩码时只会更新本属性，**不会**自动落盘 ——
        /// 必须再按右侧「应用」才写入 bindings.json 并下发，这与「不要一改就自动应用」的约定一致。
        /// </summary>
        public string Selection
        {
            get => _selection;
            set
            {
                if (value == _selection)
                    return;
                _selection = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selection)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            }
        }

        private string _selection = "不绑定";

        /// <summary>
        /// 已保存的优先级档位。空串 = 这条规则不管优先级（旧规则 / 只做了掩码调度）。
        /// </summary>
        public string Priority { get; set; } = string.Empty;

        private string _prioritySelection = ProcessBindingPage.PriorityNormal;

        /// <summary>
        /// 优先级下拉的选中项。
        ///
        /// 与运行中进程那边的区别：这里改的是「下次启动时应用的值」，
        /// 必须点「应用」才落盘并下发 —— 和掩码下拉的行为保持一致。
        /// 两条下拉一个即时生效、一个要点确认，看似不一致，但那是刻意的：
        /// 掩码要写文件、要影响所有未来实例，优先级只影响当前活着的进程。
        /// </summary>
        public string PrioritySelection
        {
            get => _prioritySelection;
            set
            {
                if (value == _prioritySelection || string.IsNullOrEmpty(value))
                    return;
                _prioritySelection = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrioritySelection)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            }
        }

        /// <summary>
        /// 下拉当前值与已保存值不一致 → 表示有待应用的改动。
        ///
        /// 优先级也算在内：只改优先级不改掩码时，「应用」按钮应当可用。
        /// 否则用户改完优先级发现点不了应用，会以为改动没被接受。
        /// </summary>
        public bool IsDirty =>
            !string.Equals(_selection, Label, StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(Priority) && !string.Equals(_prioritySelection, Priority, StringComparison.Ordinal));

        /// <summary>名称下方的次要信息：路径 + （停用时）状态说明。</summary>
        public string SubText
        {
            get
            {
                string path = string.IsNullOrWhiteSpace(Path) ? "路径未记录" : Path;
                return Enabled ? path : path + "　·　已停用";
            }
        }

        public Visibility SubTextVisibility =>
            string.IsNullOrWhiteSpace(SubText) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>名称列的悬停提示（名称 + 完整路径）。</summary>
        public string ToolTipText => string.IsNullOrWhiteSpace(Path) ? Name : Name + "\r\n" + Path;

        private bool _enabled = true;

        /// <summary>是否启用；切换时通过 <see cref="EnabledChanged"/> 通知页面落盘。</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubText)));
                EnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 启用状态对应的按钮文案。
        /// 按钮显示的是"点了会发生什么"：当前已启用 → 显示「禁用」；已禁用 → 显示「启用」。
        /// </summary>
        public string EnableLabel => Enabled ? "禁用" : "启用";

        /// <summary>按钮的悬停说明，随状态切换。</summary>
        public string EnableHint => Enabled
            ? "禁用后本规则不再参与自动绑定（记录保留，随时可重新启用）"
            : "启用后本规则恢复自动绑定，并对正在运行的实例立即生效";

        /// <summary>把启用状态变更通知界面刷新按钮文案。</summary>
        public void NotifyEnabledChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubText)));
        }

        public event EventHandler? EnabledChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 进程绑定页：按进程名合并列表，选择目标即绑定该名称下所有 PID，
    /// 规则按「进程名 → 目标」持久化；自定义掩码由独立窗口管理并动态进入下拉框。
    /// </summary>
    public partial class ProcessBindingPage : Page
    {
        /// <summary>下拉选项（不绑定 / CCD0 / CCD1 / 各自定义掩码名称），全局共享并通知所有 ComboBox。</summary>
        public static ObservableCollection<string> TargetOptions { get; } = new();

        // ---------------- 优先级下拉 ----------------
        //
        // 【为什么不给 RealTime】
        // RealTime（pcPriClassBase = 24）高于系统输入线程。一个忙等的 RealTime 进程
        // 可以让鼠标键盘完全失去响应，用户只能等它自己结束或按电源键。
        // 任务管理器里那个「实时」选项我们也刻意不提供 —— 不是能力做不到，
        // 是这个选项的失败代价与它的收益完全不成比例。
        //
        // 四个标签必须与 PriorityLabel() 的输出严格一致：
        // 下拉是双向绑定，回填时拿 PriorityLabel 的结果去匹配选项，
        // 少一个字就匹配不上、下拉显示空白。

        public const string PriorityHigh = "High";
        public const string PriorityAboveNormal = "Above Normal";
        public const string PriorityNormal = "Normal";
        public const string PriorityBelowNormal = "Below Normal";
        public const string PriorityLow = "Low";

        /// <summary>优先级下拉的选项，顺序由高到低（与页面其他下拉一致）。</summary>
        public static ObservableCollection<string> PriorityOptions { get; } = new()
        {
            PriorityHigh,
            PriorityAboveNormal,
            PriorityNormal,
            PriorityBelowNormal,
            PriorityLow,
        };

        /// <summary>
        /// 档位标签 → SetPriorityClass 常量。
        /// 返回 0 表示这个标签不对应任何可写档位（自定义数值 / 未知值），调用方应跳过下发。
        /// </summary>
        private const uint PRIORITY_CLASS_IDLE = 0x00000040;
        private const uint PRIORITY_CLASS_BELOW_NORMAL = 0x00004000;
        private const uint PRIORITY_CLASS_NORMAL = 0x00000020;
        private const uint PRIORITY_CLASS_ABOVE_NORMAL = 0x00008000;
        private const uint PRIORITY_CLASS_HIGH = 0x00000080;

        private static uint PriorityClassOf(string label) => label switch
        {
            PriorityHigh => PRIORITY_CLASS_HIGH,
            PriorityAboveNormal => PRIORITY_CLASS_ABOVE_NORMAL,
            PriorityNormal => PRIORITY_CLASS_NORMAL,
            PriorityBelowNormal => PRIORITY_CLASS_BELOW_NORMAL,
            PriorityLow => PRIORITY_CLASS_IDLE,
            _ => 0,
        };

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// 把一家子进程都设成指定优先级。
        ///
        /// 只提升不降级？不 —— 用户选什么就设什么，包括降级。
        /// 「后台隔离」这类用法本来就是把 QQ / 浏览器降到 Below Normal，
        /// 只给升不给降等于砍掉一半用途。
        ///
        /// 返回 (成功数, 失败数)。失败通常是权限不足（系统进程、受保护进程）
        /// 或进程已退出，两种情况都不需要向用户弹窗。
        /// </summary>
        private static (int Ok, int Failed) ApplyPriority(IEnumerable<ProcessMember> members, string label)
        {
            uint cls = PriorityClassOf(label);
            if (cls == 0)
                return (0, 0);

            int ok = 0, failed = 0;

            foreach (ProcessMember member in members)
            {
                IntPtr handle = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
                                            false, member.Pid);
                if (handle == IntPtr.Zero)
                {
                    failed++;
                    continue;
                }

                try
                {
                    if (SetPriorityClass(handle, cls))
                        ok++;
                    else
                        failed++;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            return (ok, failed);
        }

        private readonly ObservableCollection<ProcessGroupRow> _groups = new();
        private readonly List<ProcessGroupRow> _allGroups = new();
        private readonly ObservableCollection<SavedRuleRow> _savedRules = new();

        /// <summary>当前是否按 CPU 从高到低排序（默认 true）。</summary>
        private bool _sortByCpuDescending = true;

        /// <summary>规则列表重建期间抑制开关回调，避免误写盘。</summary>
        private bool _loadingRules;

        /// <summary>本次聚焦是否已经做过一次全选，避免每敲一个字都被重新选中。</summary>
        private bool _searchSelectDone;

        /// <summary>CPU Sets 拓扑是否可用（false 时禁用所有绑定入口）。</summary>
        private bool _topologyAvailable = true;

        public ProcessBindingPage()
        {
            InitializeComponent();
            ProcessListView.ItemsSource = _groups;
            SavedRulesList.ItemsSource = _savedRules;

            MaskStore.Load();
            SyncTargetOptions();
            // MaskStore.Changed 是静态事件，订阅移到 Loaded / Unloaded 成对处理。
            // 原先只在构造函数订阅、Unloaded 里不解绑，页面每被导航打开一次就
            // 永久多挂一个回调：旧页面的 OnMasksChanged 会一直持有已卸载页面的
            // 控件引用（阻止回收），改名 / 删掩码时还会对死控件做一次 Dispatcher 调用。

            // 搜索框聚焦时加速刷新（1s），失焦恢复默认（3s，与 ProcessRefresher 的默认值一致）
            SearchBox.GotFocus += (_, _) => ProcessRefresher.SetInterval(1);
            SearchBox.LostFocus += (_, _) => ProcessRefresher.SetInterval(3);

            // 输入法组字高亮：中文输入法组字阶段会用系统高亮色把正在输入的那一段
            // 涂成实心色块，看起来就是"高亮不合理"。这里把 SelectionOpacity 调低，
            // 选中/组字底色变淡，文字仍然可读，不再出现整块实心色。
            SearchBox.SelectionOpacity = 0.22;
            // 保留输入法（中文搜索需要），不关闭。
            InputMethod.SetIsInputMethodEnabled(SearchBox, true);

            // Ctrl+F 聚焦搜索框（常规习惯）
            CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Find,
                (_, e) =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                }));

            // 下拉框的展开 / 收起是普通 CLR 事件（不是路由事件），无法在列表容器上
            // 统一 AddHandler，改由行模板里的 ComboBox 各自挂（见 XAML）。
            // 键盘焦点离开列表后，也要补一次被交互推迟的刷新。
            // ItemsControl 没有 ListView 的列表级键盘语义，但「焦点离开列表」这个
            // 时机仍然需要补一次被推迟的刷新。改用 PreviewLostKeyboardFocus，
            // 它在 ItemsControl 上同样可用。
            ProcessListView.LostKeyboardFocus += ProcessListView_LostKeyboardFocus;

            Loaded += async (_, _) =>
            {
                _pageLoaded = true;

                // 订阅必须和 Unloaded 里的解绑成对出现。
                // 以前只在构造函数里订阅一次，而 Unloaded 会解绑，
                // 一旦同一实例再被 Loaded（页面被导航缓存复用）就再也收不到快照了。
                ProcessRefresher.Refreshed -= OnProcessesRefreshed;
                ProcessRefresher.Refreshed += OnProcessesRefreshed;

                // 同上：先减后加，避免同一实例被复用后重复订阅
                MaskStore.Changed -= OnMasksChanged;
                MaskStore.Changed += OnMasksChanged;

                AttachWindowWatchers();
                UpdateWatchState();

                CheckTopology();
                LoadSavedRules();

                // 立即用最新快照刷新一次。若快照为空（第一次打开、采样循环此前一直挂起），
                // 不要喂空集合给 ApplySnapshot —— 那会把列表清成空白，
                // 紧接着第一次采样回来又填满，看起来就是「闪一下」。
                IReadOnlyList<ProcessRefresher.Entry> latest = ProcessRefresher.Latest;
                if (latest.Count > 0)
                {
                    // 分组放在后台算：首次进页面往往有 200+ 进程，
                    // 在 Loaded 里同步分组会让「切到进程页」明显顿一下。
                    List<GroupedProcess> grouped = await Task.Run(() => GroupEntries(latest));
                    ApplySnapshot(grouped);
                }

                // 搜索框占位提示：统一由 UpdateSearchPlaceholder 决定
                UpdateSearchPlaceholder();
            };
            Unloaded += (_, _) =>
            {
                ProcessRefresher.Refreshed -= OnProcessesRefreshed;
                MaskStore.Changed -= OnMasksChanged;

                _pageLoaded = false;
                DetachWindowWatchers();
                UpdateWatchState();
            };
        }

        // ==================== 采样启停：只在「真的有人看」时采集 ====================

        private bool _pageLoaded;
        private bool _watching;
        private Window? _watchedWindow;

        /// <summary>
        /// 挂上主窗口的「可见性 / 最小化」监听。
        /// 只挂一次，Unloaded 时摘掉，避免重复订阅。
        /// </summary>
        private void AttachWindowWatchers()
        {
            Window? owner = Window.GetWindow(this);
            if (owner == null || ReferenceEquals(owner, _watchedWindow))
                return;

            DetachWindowWatchers();
            _watchedWindow = owner;
            owner.IsVisibleChanged += OnOwnerVisibilityChanged;
            owner.StateChanged += OnOwnerStateChanged;
        }

        private void DetachWindowWatchers()
        {
            if (_watchedWindow == null)
                return;

            _watchedWindow.IsVisibleChanged -= OnOwnerVisibilityChanged;
            _watchedWindow.StateChanged -= OnOwnerStateChanged;
            _watchedWindow = null;
        }

        private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
            => UpdateWatchState();

        private void OnOwnerStateChanged(object? sender, EventArgs e)
            => UpdateWatchState();

        /// <summary>
        /// 三个条件全满足才算「真的有人在看」：页面已加载、窗口可见、未最小化。
        /// 任一不满足就释放观察权，采样器随即挂起（不是降频，是彻底停下）。
        /// </summary>
        private void UpdateWatchState()
        {
            // Loaded 时 Window.GetWindow 偶尔还拿不到宿主窗口（页面刚挂进 Frame），
            // 此时不采集就等于「打开页面却一直没有数据」。这里补一次尝试。
            if (_pageLoaded && _watchedWindow == null)
                AttachWindowWatchers();

            Window? owner = _watchedWindow;
            bool shouldWatch = _pageLoaded
                && owner != null
                && owner.IsVisible
                && owner.WindowState != WindowState.Minimized;

            if (shouldWatch == _watching)
                return;

            _watching = shouldWatch;

            if (shouldWatch)
                ProcessRefresher.AcquireWatch();
            else
                ProcessRefresher.ReleaseWatch();
        }

        /// <summary>
        /// 列宽不再需要手工分配。
        ///
        /// 上一版用 ListView + GridView，列宽是死值，窗口变宽时只能靠
        /// ApplyColumnWidths 逐列改写 Width，还要配一层尺寸去抖防止
        /// 「改宽度 → 触发布局 → 再次 SizeChanged」的抖动循环。
        /// v12 改成自绘 Grid 后，列宽写成 1.5* / 2* / 固定值，
        /// 弹性分配由 WPF 布局引擎完成，那段代码整体删除。
        ///
        /// 保留这个空方法只是为了让构造函数里那行调用不至于悬空 —— 一并删掉更干净。
        /// </summary>

        /// <summary>
        /// 检测 CPU Sets 拓扑。不可用时：
        ///   1. 页面顶部显示红色警告横幅；
        ///   2. 进程列表的绑定下拉框全部置灰（行对象 CanBind=false）；
        ///   3. 所有绑定入口弹窗提示规则无法生效。
        /// 绝不静默失败。
        /// </summary>
        private void CheckTopology()
        {
            _topologyAvailable = CpuSetBinder.IsTopologyAvailable;

            // Z-4：拓扑不可用 → 逐行置灰绑定下拉框（配合 XAML 的 IsEnabled={Binding CanBind}）。
            // 新建行也会按当前拓扑状态取初值（见 ApplySnapshot）。
            foreach (ProcessGroupRow g in _allGroups)
                g.CanBind = _topologyAvailable;

            TopologyWarningBanner.Visibility = _topologyAvailable
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (_topologyAvailable)
            {
                TopologyWarningDetail.Text = string.Empty;
                return;
            }

            TopologyWarningDetail.Text = CpuSetBinder.LastTopologyError ?? "未能枚举到任何逻辑处理器。";
            LogService.Error(
                $"CPU Sets 拓扑不可用，绑定下拉框已置灰：{CpuSetBinder.LastTopologyError}");
        }

        // ---------- 下拉选项 ----------

        /// <summary>
        /// 与 MaskStore 同步下拉选项（不绑定 / CCD0 / CCD1 / 各自定义掩码）。
        /// 采用增量同步而非 Clear()，避免 WPF 在清空集合时把选中项写回 null、
        /// 从而误删用户已选的自定义掩码规则。
        /// </summary>
        private static void SyncTargetOptions()
        {
            if (!TargetOptions.Contains("不绑定")) TargetOptions.Add("不绑定");
            if (!TargetOptions.Contains("CCD0")) TargetOptions.Add("CCD0");
            if (!TargetOptions.Contains("CCD1")) TargetOptions.Add("CCD1");

            foreach (CustomMask mask in MaskStore.Masks)
                if (!string.IsNullOrWhiteSpace(mask.Name) && !TargetOptions.Contains(mask.Name))
                    TargetOptions.Add(mask.Name);

            // 移除已删除的掩码名（索引 3 之后）
            for (int i = TargetOptions.Count - 1; i >= 3; i--)
            {
                string name = TargetOptions[i];
                if (!MaskStore.Masks.Any(m => m.Name == name))
                    TargetOptions.RemoveAt(i);
            }
        }

        /// <summary>掩码增删改名后：同步下拉并丢弃已失效的选择（触发规则删除）。</summary>
        private void OnMasksChanged()
        {
            Dispatcher.Invoke(() =>
            {
                SyncTargetOptions();
                foreach (ProcessGroupRow group in _allGroups)
                {
                    if (!TargetOptions.Contains(group.Selection))
                        group.Selection = "不绑定";
                }
                LoadSavedRules();
            });
        }

        // ---------- 进程枚举与合并 ----------

        /// <summary>
        /// 全局后台采样回调：收到新快照就更新列表。
        ///
        /// 【为什么分组要挪到后台线程】
        /// 快照可能有 200+ 个进程，GroupBy + 求和 + 建 ProcessMember 是纯 CPU 工作，
        /// 在 UI 线程上跑会直接吞掉一帧（低端机上 30~60ms），表现为「切到进程页
        /// 或每 2 秒卡一下」。搬到线程池后 UI 线程只负责把结果写进 ObservableCollection
        /// —— 那一步必须回 UI 线程，因为 ObservableCollection 的 CollectionChanged
        /// 会被 ItemsControl 直接消费，跨线程改会抛 NotSupportedException。
        ///
        /// 重入保护：采样周期 1~3 秒，算一次通常几十毫秒，理论上排不到队；
        /// 但机器很忙时可能出现「上一轮还没算完下一轮又来」，用一个标志丢掉
        /// 中间的结果，只保证最新一帧落地 —— 列表只需要最新状态。
        /// </summary>
        private int _snapshotBusy;

        private async void OnProcessesRefreshed(IReadOnlyList<ProcessRefresher.Entry> entries)
        {
            if (System.Threading.Interlocked.Exchange(ref _snapshotBusy, 1) == 1)
                return;

            try
            {
                List<GroupedProcess> grouped = await Task.Run(() => GroupEntries(entries));
                ApplySnapshot(grouped);
            }
            catch (Exception ex)
            {
                LogService.Warn($"进程列表刷新失败：{ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _snapshotBusy, 0);
            }
        }

        /// <summary>后台线程算出来的分组结果（不持有任何 UI 对象）。</summary>
        private sealed class GroupedProcess
        {
            public string Name = string.Empty;
            public int Count;
            public double Cpu;
            public string Path = string.Empty;
            public string PriorityText = string.Empty;
            public List<ProcessMember> Members = new();
        }

        /// <summary>纯计算，可在任意线程调用。</summary>
        private static List<GroupedProcess> GroupEntries(IReadOnlyList<ProcessRefresher.Entry> entries)
        {
            List<GroupedProcess> result = new();

            foreach (IGrouping<string, ProcessRefresher.Entry> grp in
                     entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                List<ProcessMember> members = grp
                    .Select(e => new ProcessMember { Pid = e.Pid, Path = e.Path, PriorityClass = e.PriorityClass })
                    .ToList();

                int count = members.Count;

                result.Add(new GroupedProcess
                {
                    Name = grp.Key,
                    Count = count,
                    Cpu = count > 0 ? grp.Sum(e => e.Cpu) / count : 0.0,
                    Path = grp.FirstOrDefault(e => !string.IsNullOrEmpty(e.Path))?.Path ?? string.Empty,
                    PriorityText = PriorityLabel(members.Count > 0 ? members[0].PriorityClass : 0),
                    Members = members
                });
            }

            return result;
        }

        /// <summary>
        /// 把后台算好的分组合并进列表（就地更新，保留用户已选目标，不闪屏）。
        ///
        /// 这一步必须在 UI 线程：_groups 是 ObservableCollection，
        /// 写入会触发 ItemsControl 的集合变更，跨线程操作会抛异常。
        /// </summary>
        private void ApplySnapshot(List<GroupedProcess> grouped)
        {
            // 用稳定字典合并：已存在的进程名就地更新（保留用户已选 Selection 与下拉选择），
            // 新名称新增、消失的名称移除，避免每次刷新整体重建导致下拉选择跳回 / 闪屏。
            Dictionary<string, ProcessGroupRow> existing =
                _allGroups.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            List<ProcessGroupRow> merged = new(grouped.Count);

            foreach (GroupedProcess grp in grouped)
            {
                if (existing.TryGetValue(grp.Name, out ProcessGroupRow? row))
                {
                    // 就地更新数值，保留 Selection（含下拉选中项）
                    row.Count = grp.Count;
                    row.CpuValue = grp.Cpu;
                    row.Members = grp.Members;
                    row.Path = grp.Path;
                    row.PriorityText = grp.PriorityText;
                }
                else
                {
                    row = new ProcessGroupRow
                    {
                        Name = grp.Name,
                        Count = grp.Count,
                        CpuValue = grp.Cpu,
                        Members = grp.Members,
                        Path = grp.Path,
                        CanBind = _topologyAvailable,
                        PriorityText = grp.PriorityText
                    };
                    // 新建行才用已保存规则回填初始选择（不触发绑定）
                    row.SetInitialSelection(RuleToSelection(BindingStore.LookupByName(grp.Name)));
                    row.TargetChanged += OnGroupTargetChanged;
                    row.PriorityChanged += OnGroupPriorityChanged;

                    // 回填优先级：规则里存过就用规则的值，没存过用进程的实时值。
                    // 「规则优先」是有意的 —— 用户上次设成 High，进程重启后仍是 Normal，
                    // 这时显示进程实时值会让用户以为自己的设置丢了。
                    SavedRuleRow? saved = _savedRules.FirstOrDefault(
                        r => string.Equals(r.Name, grp.Name, StringComparison.OrdinalIgnoreCase));

                    // 【这里必须走映射，不能直接把 grp.PriorityText 塞进去】
                    // 实时值可能是 "RealTime"、裸数字或 "—"，都不在下拉选项里；
                    // 直接塞的话 SelectedItem 绑不上，下拉显示空白 —— 看起来像
                    // "这个进程没有优先级"。PriorityLabelToOption 会把它们归到 Normal。
                    row.SetInitialPriority(
                        saved != null && !string.IsNullOrEmpty(saved.Priority)
                            ? saved.Priority
                            : PriorityLabelToOption(grp.PriorityText));
                }

                merged.Add(row);
            }

            _allGroups.Clear();
            _allGroups.AddRange(merged);

            ApplyFilter();
        }

        /// <summary>用户正在与列表交互。这个状态下重排会让正在点的那一行跳走。</summary>
        private bool _comboOpen;

        /// <summary>交互期间被推迟的一次后台刷新，等手松开后补做。</summary>
        private bool _pendingListRefresh;

        /// <summary>
        /// 按搜索词过滤，并按 CPU 占用排序后写入可见集合。
        /// </summary>
        /// <param name="force">
        /// true 表示用户主动触发（点了列头排序、改了搜索词），必须立刻响应；
        /// false 表示后台快照自动刷新，会在用户正在操作时让位。
        /// </param>
        /// <remarks>
        /// 这个方法每 2 秒被驱动一次，实现上是 Clear 整个可见集合再按占用重排后重新 Add。
        /// 而列表默认按占用降序，所以占用值每抖一下，行的位置就变一次。
        /// 用户伸手去点某一行的下拉框时若正好赶上一次刷新，就会点空或者绑错进程。
        /// 这里不改重排机制，只在「用户正在操作」时推迟后台那一次，手松开再补。
        /// </remarks>
        private void ApplyFilter(bool force = false)
        {
            if (!force && IsListInteracting())
            {
                _pendingListRefresh = true;
                return;
            }

            _pendingListRefresh = false;

            string query = (SearchBox.Text ?? string.Empty).Trim().ToLowerInvariant();

            IEnumerable<ProcessGroupRow> rows = _allGroups;
            if (query.Length > 0)
                rows = rows.Where(g => g.Name.ToLowerInvariant().Contains(query));

            // 默认高占用在前；同占用时按名称排，保证顺序稳定
            rows = _sortByCpuDescending
                ? rows.OrderByDescending(g => g.CpuValue).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(g => g.CpuValue).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);

            // 增量同步：原地增删 + 重排，避免每次刷新整列表重建
            // （Clear+Add 会让 ListView 重建设施，造成闪屏、滚动跳变、焦点丢失）。
            // 同一进程名的行对象在 _allGroups 中被复用，容器随之复用，无闪烁。
            List<ProcessGroupRow> desired = rows.ToList();
            for (int i = _groups.Count - 1; i >= 0; i--)
                if (!desired.Contains(_groups[i]))
                    _groups.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                int idx = _groups.IndexOf(desired[i]);
                if (idx == -1)
                    _groups.Insert(i, desired[i]);
                else if (idx != i)
                    _groups.Move(idx, i);
            }

            EmptyHint.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>此刻用户是否正在列表上操作（展开下拉框、键盘选行、鼠标按下未松）。</summary>
        private bool IsListInteracting()
        {
            if (_comboOpen)
                return true;
            if (ProcessListView != null && ProcessListView.IsKeyboardFocusWithin)
                return true;
            return Mouse.LeftButton == MouseButtonState.Pressed;
        }

        /// <summary>交互结束后补做被推迟的那次刷新。</summary>
        private void FlushPendingRefresh()
        {
            if (!_pendingListRefresh)
                return;

            _pendingListRefresh = false;
            ApplyFilter(true);
        }

        private void TargetCombo_DropDownOpened(object sender, EventArgs e) => _comboOpen = true;

        private void TargetCombo_DropDownClosed(object sender, EventArgs e)
        {
            _comboOpen = false;
            FlushPendingRefresh();
        }

        private void ProcessListView_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
            => FlushPendingRefresh();

        /// <summary>
        /// 点击「占用」列头切换升序 / 降序。
        ///
        /// v12 改自绘表头后没有 GridViewColumnHeader 了，排序入口挂在
        /// 表头里那个「占用」TextBlock 上（XAML 里 MouseLeftButtonUp）。
        /// 排序方向的变化同时要反映到列头文字上，否则用户看不出当前是升还是降。
        /// </summary>
        private void CpuHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _sortByCpuDescending = !_sortByCpuDescending;

            if (sender is TextBlock header)
            {
                header.Text = _sortByCpuDescending ? "占用 ▼" : "占用 ▲";
                header.ToolTip = _sortByCpuDescending
                    ? "点击切换排序方向（当前：从高到低）"
                    : "点击切换排序方向（当前：从低到高）";
            }

            // 用户主动排序，立即生效，不受交互保护推迟
            ApplyFilter(true);
            e.Handled = true;
        }

        /// <summary>
        /// 点击进程行：选中该行（v12 的 process-row.active 视觉）。
        ///
        /// 自绘行没有 ListBox 的选中态，这里自己记一个选中行对象，
        /// 再把该行的 Border 底色改成强调色淡底。只做视觉反馈，
        /// 不改变任何绑定语义 —— 点行不等于改掩码，改掩码只有下拉框能做。
        /// </summary>
        private void ProcessRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border clicked)
                return;

            // 点在下拉框上时不要抢走它自己的点击
            if (e.OriginalSource is DependencyObject src && FindAncestor<ComboBox>(src) != null)
                return;

            foreach (Border row in FindVisualChildren<Border>(ProcessListView))
                row.ClearValue(Border.BackgroundProperty);

            clicked.Background = (Brush)FindResource("AccentSoftBrush");
        }

        /// <summary>向上找指定类型的祖先（点到下拉框内部时不抢事件用）。</summary>
        private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            for (DependencyObject? cur = node; cur != null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is T match)
                    return match;
            }
            return null;
        }

        /// <summary>深度遍历可视树，收集指定类型的子元素。</summary>
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    yield return match;

                foreach (T nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }

        /// <summary>
        /// pcPriClassBase → 可读标签。
        ///
        /// 这是「基础优先级类」，取值只有几档固定值（4/6/8/10/13/24/32），
        /// 不随线程动态优先级浮动。未知值原样带出，不猜。
        /// </summary>
        private static string PriorityLabel(int pcPriClassBase) => pcPriClassBase switch
        {
            4 => "Idle",
            6 => "Below Normal",
            8 => "Normal",
            10 => "Above Normal",
            13 => "High",
            24 => "RealTime",
            32 => "RealTime",
            _ => pcPriClassBase > 0 ? pcPriClassBase.ToString() : "—"
        };

        // ---------- 规则解析 ----------

        /// <summary>目标的人类可读名称：CCD 直接写名字，自定义走掩码名 / 掩码区间文本。</summary>
        private static string TargetLabel(BindingTarget target, string maskId, ulong mask) => target switch
        {
            BindingTarget.CCD0 => "CCD0",
            BindingTarget.CCD1 => "CCD1",
            _ => MaskStore.FindById(maskId)?.Name ?? MaskFormatter.ToRangeText(mask)
        };

        private static string RuleToSelection(BindingRule? rule)
        {
            if (rule == null || rule.Target == BindingTarget.None)
                return "不绑定";
            if (rule.Target == BindingTarget.CCD0) return "CCD0";
            if (rule.Target == BindingTarget.CCD1) return "CCD1";

            // Custom：用掩码名回填（若掩码已被删除则回落不绑定）
            string? maskName = MaskStore.FindById(rule.MaskId)?.Name;
            return string.IsNullOrWhiteSpace(maskName) ? "不绑定" : maskName;
        }

        private static (BindingTarget Target, string MaskId, ulong Mask) SelectionToTarget(string sel)
        {
            if (sel == "CCD0") return (BindingTarget.CCD0, string.Empty, 0);
            if (sel == "CCD1") return (BindingTarget.CCD1, string.Empty, 0);
            if (sel == "不绑定" || string.IsNullOrWhiteSpace(sel)) return (BindingTarget.None, string.Empty, 0);

            CustomMask? mask = MaskStore.FindByName(sel);
            return mask != null
                ? (BindingTarget.Custom, mask.Id, mask.Mask)
                : (BindingTarget.None, string.Empty, 0);
        }

        // ---------- 事件 ----------

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchPlaceholder();
            // 打字是用户主动行为，过滤必须立刻跟上，不能被交互保护推迟
            ApplyFilter(true);
        }

        /// <summary>
        /// 占位文字的显示规则：只有在"没有内容 且 没有焦点"时才显示。
        /// 获得焦点后立刻隐去，避免光标压在占位文字上（之前看起来像光标跑到了文字中间）。
        /// </summary>
        private void UpdateSearchPlaceholder()
        {
            if (SearchPlaceholder == null || SearchBox == null)
                return;

            bool empty = string.IsNullOrEmpty(SearchBox.Text);
            bool focused = SearchBox.IsKeyboardFocusWithin;
            SearchPlaceholder.Visibility = (empty && !focused) ? Visibility.Visible : Visibility.Collapsed;

            // 打开（点进来）时如果有内容，直接全选：下次输入即覆盖，
            // 不用先手动清空，符合"打开搜索框后文字应当消去"的期望。
            if (focused && !empty && !_searchSelectDone)
            {
                _searchSelectDone = true;
                SearchBox.Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() => SearchBox.SelectAll()));
            }
            else if (!focused)
            {
                _searchSelectDone = false;
            }
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateSearchPlaceholder();
            UpdateSearchIcon();
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateSearchPlaceholder();
            UpdateSearchIcon();
        }

        /// <summary>
        /// 聚焦时把放大镜图标染成强调色，作为「已选中此输入框」的轻提示。
        /// 不去加粗整个边框：那正是之前看起来"颜色过重"的原因。
        /// </summary>
        private void UpdateSearchIcon()
        {
            if (SearchIcon == null)
                return;

            object key = SearchBox.IsKeyboardFocusWithin ? "AccentBrush" : "Text.Secondary";
            if (TryFindResource(key) is System.Windows.Media.Brush brush)
                SearchIcon.Stroke = brush;
        }

        /// <summary>Esc 清空搜索并退出焦点，符合搜索框的常规预期。</summary>
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Clear();
                e.Handled = true;
            }
            else
            {
                // 已经是空的了：把焦点还给列表，方便直接键盘操作
                ProcessListView.Focus();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 某一组的选择变更：立即热绑定其名下所有 PID，并持久化规则。
        ///
        /// 这里不等后台 1 秒轮询——保存即下发，下发完立刻弹窗告知结果。
        /// 拓扑不可用时直接拦截并弹窗，绝不让用户以为绑定成功了。
        /// </summary>
        private void OnGroupTargetChanged(object? sender, EventArgs e)
        {
            if (sender is not ProcessGroupRow group)
                return;

            (BindingTarget target, string maskId, ulong mask) = SelectionToTarget(group.Selection);

            // 解除绑定（不绑定）永远允许，不受拓扑状态影响
            if (target != BindingTarget.None && !EnsureTopologyOrWarn())
            {
                group.SetInitialSelection("不绑定");
                return;
            }

            if (target != BindingTarget.None)
            {
                string label = TargetLabel(target, maskId, mask);

                // 热绑定：立刻遍历该进程名下所有运行实例并下发
                int bound = BindGroupMembers(group, target, maskId, mask, label);

                string msg = bound > 0
                    ? $"规则已保存，并对 {bound} 个正在运行的进程立即生效（{group.Name} → {label}）。"
                    : $"规则已保存：{group.Name} → {label}。当前该进程没有正在运行的实例，下次启动后会自动生效。";

                LogService.Info(msg);
                // 普通反馈 → Toast（规则已保存，用户不需要做任何决定）
                ToastService.Success(msg);
            }

            UpsertRule(group.Name, target, maskId, mask, group.Path);
            ReloadRulesAfterChange();
            _ = FlashRowAsync(group);
        }

        /// <summary>
        /// 用户改了某一行进程的优先级：立刻对名下所有实例下发。
        ///
        /// 【为什么不落盘】优先级不是"绑定规则"的一部分，它是进程的运行属性。
        /// 进程退出后 OS 会回收，下次启动自然回到 Normal。
        /// 如果把它存进 bindings.json，就会变成"我明明改了游戏，怎么重启又变回去了"
        /// 这种更难解释的问题 —— 存了但只在启动时才同步，等于没存。
        ///
        /// 例外：如果这个进程名本来就有保存的调度规则，则顺手把优先级写进规则，
        /// 让"后台常驻调度"在新实例起来时一起应用。这条路径见 UpsertRule 的调用方。
        /// </summary>
        private void OnGroupPriorityChanged(object? sender, EventArgs e)
        {
            if (sender is not ProcessGroupRow group)
                return;

            (int ok, int failed) = ApplyPriority(group.Members, group.PrioritySelection);

            if (ok == 0 && failed == 0)
                return;

            if (ok == 0)
            {
                // 全部失败：通常是系统进程 / 受保护进程，没有权限改。
                // 不弹窗 —— 用户点一下就知道结果，弹窗反而打断。
                LogService.Warn($"设置优先级失败：{group.Name}（{failed} 个实例）");
                ToastService.Error($"无法修改 {group.Name} 的优先级（权限不足或进程已退出）");
                return;
            }

            LogService.Info($"优先级已设置：{group.Name} → {group.PrioritySelection}（{ok} 个实例）");

            if (failed > 0)
                ToastService.Info($"{group.Name} → {group.PrioritySelection}：{ok} 个成功，{failed} 个无权修改");
            else
                ToastService.Success($"{group.Name} → {group.PrioritySelection}");

            // 有保存规则的进程，把优先级一并记进规则，新实例启动时会被 ProcessWatcher 应用。
            if (_savedRules.Any(r => string.Equals(r.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            {
                UpsertRule(group.Name, SelectionToTarget(group.Selection).Target,
                           SelectionToTarget(group.Selection).MaskId,
                           SelectionToTarget(group.Selection).Mask,
                           group.Path,
                           group.PrioritySelection);
                ReloadRulesAfterChange();
            }
        }

        /// <summary>
        /// 拓扑可用性拦截。不可用时弹出明确警告并返回 false，绝不静默失败。
        /// </summary>
        private bool EnsureTopologyOrWarn()
        {
            if (CpuSetBinder.IsTopologyAvailable)
                return true;

            CheckTopology();
            // 顶部的警告条已经把这件事故障原因常驻显示在页面上，
            // 这里再弹一个必须点确定的框只是重复告知 —— 走 Toast 即可。
            ToastService.Error("CPU Sets 拓扑不可用，规则无法生效，请以管理员身份重新运行");
            return false;
        }

        /// <summary>
        /// 对某一进程名下所有运行实例下发绑核，返回成功绑定的进程数。
        /// 纯粹的 CPU Sets 调用——不退化成 ProcessorAffinity。
        /// </summary>
        /// <summary>按目标类型挑一路下发。CCD 绑定用不到掩码，只在 Custom 时才有意义。</summary>
        private static CpuSetBindResult BindMember(int pid, BindingTarget target, ulong mask) => target switch
        {
            BindingTarget.CCD0 => CpuSetBinder.BindToCcd(pid, 0),
            BindingTarget.CCD1 => CpuSetBinder.BindToCcd(pid, 1),
            _ => CpuSetBinder.BindToMask(pid, mask)
        };

        private static int BindGroupMembers(
            ProcessGroupRow group, BindingTarget target, string maskId, ulong mask, string label)
        {
            int bound = 0;
            int failed = 0;

            foreach (ProcessMember m in group.Members)
            {
                CpuSetBindResult result = BindMember(m.Pid, target, mask);

                if (result.Success)
                {
                    bound++;
                    LogService.Info($"已绑定 {group.Name}（PID {m.Pid}）→ {label}，{result.AppliedCount} 个 CPU Set");
                }
                else
                {
                    failed++;

                    // 【级别按成因区分，不是一律 ERROR】
                    // 「进程在绑定的这一瞬间退出了」是最常见的失败：用户点了应用，
                    // 某个标签页刚好关掉，PID 不存在。这是竞态，不是执行失败，
                    // 报 ERROR 会让日志页红一片而用户完全无法行动。
                    // 只有「进程还活着但拿不到权限 / API 拒绝」才是真正的执行失败。
                    if (IsTransientBindFailure(result.Error))
                        LogService.Warn($"绑定跳过 {group.Name}（PID {m.Pid}）：进程已退出");
                    else
                        LogService.Error($"绑定失败 {group.Name}（PID {m.Pid}）→ {label}：{result.Error}");
                }
            }

            if (failed > 0 && bound == 0 && group.Members.Count > 0)
                LogService.Error($"{group.Name} 的全部 {failed} 个实例均未绑定成功");

            return bound;
        }

        /// <summary>
        /// 判断一次绑定失败是否属于「瞬时竞态」。
        ///
        /// 只认两条判据，都是 OpenProcess / SetProcessDefaultCpuSets 在
        /// 「目标进程已退出」时给出的确定信号：
        ///   · 错误 87  (ERROR_INVALID_PARAMETER) —— PID 已不存在；
        ///   · 错误 1168 (ERROR_NOT_FOUND)        —— PID 不匹配任何进程。
        /// 权限不足（错误 5）不在此列：那是真实的执行失败，必须报 ERROR
        /// 让用户知道要去开管理员。
        /// </summary>
        private static bool IsTransientBindFailure(string? error)
        {
            if (string.IsNullOrEmpty(error))
                return false;

            return error.Contains("错误 87", StringComparison.Ordinal)
                || error.Contains("错误 1168", StringComparison.Ordinal)
                || error.Contains("进程已退出", StringComparison.Ordinal);
        }

        /// <summary>
        /// 热绑定：按进程名从 bindings.json 取出规则，立刻对其所有运行实例下发。
        /// 供「规则开关打开」「保存规则后补绑」等场景调用，不等后台轮询。
        /// </summary>
        private int HotApplyRule(string name)
        {
            BindingRule? rule = BindingStore.LookupByName(name);
            if (rule == null || !rule.Enabled || rule.Target == BindingTarget.None)
                return 0;

            ProcessGroupRow? group = FindGroup(name);
            if (group == null)
                return 0;

            // 自定义掩码优先取实时值（用户可能刚改过掩码内容），取不到再落回快照
            ulong mask = rule.Target == BindingTarget.Custom
                ? MaskStore.FindById(rule.MaskId)?.Mask ?? rule.Mask
                : rule.Mask;

            return BindGroupMembers(group, rule.Target, rule.MaskId, mask,
                                    TargetLabel(rule.Target, rule.MaskId, mask));
        }

        /// <summary>整行闪一下青色，给「绑定指令已下发」的即时反馈。</summary>
        private async Task FlashRowAsync(ProcessGroupRow row)
        {
            row.Flash = true;
            await Task.Delay(650);
            row.Flash = false;
        }

        private void ManageMasks_Click(object sender, RoutedEventArgs e)
        {
            MaskManagerWindow window = new() { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            // 关闭后兜底重建（掩码变更已通过 Changed 事件处理）
            SyncTargetOptions();
            LoadSavedRules();
        }

        private void ApplySaved_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTopologyOrWarn())
                return;

            int bound = ApplySavedNow();
            string msg = bound > 0
                ? $"已对所有正在运行的目标进程应用绑定（共 {bound} 个）。"
                : "规则已全部就绪，但当前没有正在运行的目标进程。进程启动后会自动应用绑定。";
            LogService.Info(msg);
            // 普通反馈 → Toast
            ToastService.Success(msg);
        }

        /// <summary>按界面当前选择，对运行中的实例重新下发绑核（跳过已停用的规则）。</summary>
        private int ApplySavedNow()
        {
            int bound = 0;
            foreach (ProcessGroupRow group in _allGroups)
            {
                if (BindingStore.LookupByName(group.Name) is { Enabled: false })
                    continue;

                (BindingTarget target, string maskId, ulong mask) = SelectionToTarget(group.Selection);
                if (target == BindingTarget.None)
                    continue;

                bound += BindGroupMembers(group, target, maskId, mask,
                                          TargetLabel(target, maskId, mask));
            }

            ProcessWatcher.ResetCache();
            return bound;
        }

        /// <summary>清空全部已保存规则，并把界面上的下拉框复位为「不绑定」。</summary>
        private void ClearRules_Click(object sender, RoutedEventArgs e)
        {
            if (BindingStore.Profile.Rules.Count == 0)
            {
                // 空状态提示：用户点了个什么都不会发生的按钮，
                // 属于「知道了就行」，不需要模态拦一道。
                ToastService.Info("当前没有可清空的规则");
                return;
            }

            if (!DarkDialog.Confirm($"确定要清空全部 {BindingStore.Profile.Rules.Count} 条调度规则吗？此操作不可撤销。",
                                    "清空所有规则"))
                return;

            BindingStore.Save(new List<BindingRule>());

            foreach (ProcessGroupRow group in _allGroups)
                group.SetInitialSelection("不绑定");

            ReloadRulesAfterChange();
            LogService.Info("已清空全部调度规则");
        }

        // ---------- 规则持久化 ----------

        /// <summary>按进程名找列表里那一组（进程名比较一律忽略大小写）。</summary>
        private ProcessGroupRow? FindGroup(string name) =>
            _allGroups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>规则改动落盘后的收尾：清掉监听的「已处理」缓存，再重建规则列表。</summary>
        private void ReloadRulesAfterChange()
        {
            ProcessWatcher.ResetCache();
            LoadSavedRules();
        }

        private static void UpsertRule(string name, BindingTarget target, string maskId, ulong mask,
                                       string path = "", string priority = "")
        {
            List<BindingRule> rules = BindingStore.Profile.Rules;
            BindingRule? existing = BindingStore.LookupByName(name);

            if (target == BindingTarget.None)
            {
                if (existing != null)
                {
                    rules.Remove(existing);
                    BindingStore.Save(rules);
                }
                return;
            }

            existing ??= new BindingRule { Name = name };
            if (!rules.Contains(existing))
                rules.Add(existing);

            existing.Target = target;
            existing.MaskId = maskId;
            existing.Mask = mask;
            if (!string.IsNullOrWhiteSpace(path))
                existing.Path = path;

            // 空串 = 本次调用不碰优先级（仅改掩码时走这条路径），
            // 非空才覆盖 —— 否则改个掩码会把之前设好的优先级一起抹掉。
            if (!string.IsNullOrEmpty(priority))
                existing.Priority = priority;

            BindingStore.Save(rules);
        }

        private void LoadSavedRules()
        {
            _loadingRules = true;
            _savedRules.Clear();
            foreach (BindingRule rule in BindingStore.Profile.Rules)
            {
                if (rule.Target == BindingTarget.None)
                    continue;

                SavedRuleRow row = new()
                {
                    Name = rule.Name,
                    Label = rule.Label,
                    MaskId = rule.MaskId,
                    Enabled = rule.Enabled,
                    Path = ResolveRulePath(rule)
                };

                // 下拉框回显：与「运行中进程」同一套映射（CCD0 / CCD1 / 掩码名 / 不绑定）
                row.Selection = RuleToSelection(rule);

                // 优先级回显：规则里没存过（旧配置）就显示进程当前的实时值，
                // 而不是硬塞一个 Normal —— 用户看到 Normal 会以为规则把它的 High 改掉了。
                row.Priority = rule.Priority;
                row.PrioritySelection = string.IsNullOrEmpty(rule.Priority)
                    ? PriorityLabelToOption(FindGroup(rule.Name)?.PriorityText ?? string.Empty)
                    : rule.Priority;

                row.EnabledChanged += (s, _) => OnRuleEnabledChanged(s as SavedRuleRow);
                _savedRules.Add(row);
            }
            _loadingRules = false;

            RuleCountText.Text = $"{_savedRules.Count} 条规则";
            EmptyRulesHint.Visibility = _savedRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 把进程实时优先级的标签映射到下拉选项。
        ///
        /// 实时值可能是 "RealTime"（24/32）或裸数字 —— 这两个都不在下拉里，
        /// 一律显示 Normal。理由：下拉表示的是「我们要设成什么」，
        /// 一个我们不会去写的档位（RealTime）出现在下拉里，会让用户
        /// 以为点了能保持，实际点「应用」时 PriorityClassOf 返回 0 直接被跳过。
        /// </summary>
        private static string PriorityLabelToOption(string label) => label switch
        {
            PriorityHigh => PriorityHigh,
            PriorityAboveNormal => PriorityAboveNormal,
            PriorityNormal => PriorityNormal,
            PriorityBelowNormal => PriorityBelowNormal,
            PriorityLow => PriorityLow,
            _ => PriorityNormal,
        };

        /// <summary>优先级下拉展开 / 收起：与掩码下拉共用同一套「别在用户操作时重排」的保护。</summary>
        private void PriorityCombo_DropDownOpened(object? sender, EventArgs e) => _comboOpen = true;

        private void PriorityCombo_DropDownClosed(object? sender, EventArgs e)
        {
            _comboOpen = false;

            // 收起后补做一次被推迟的刷新（否则列表会一直停在旧数据上）
            if (_pendingListRefresh)
            {
                _pendingListRefresh = false;
                ApplyFilter();
            }
        }

        /// <summary>
        /// 取规则对应的可执行文件路径：
        /// 优先用规则里已存的 Path；没有则从当前运行实例中按进程名反查。
        /// </summary>
        private string ResolveRulePath(BindingRule rule)
        {
            if (!string.IsNullOrWhiteSpace(rule.Path))
                return rule.Path;

            return FindGroup(rule.Name)?.Path ?? string.Empty;
        }

        /// <summary>单条规则「应用」：把这一条（含行内刚改的掩码）重新下发到正在运行的实例。</summary>
        private void ApplySingleRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: SavedRuleRow row })
                return;

            if (!EnsureTopologyOrWarn())
                return;

            // 规则行里的下拉框可能已被改选：以界面选择为准先落盘，再下发。
            // 这样「改掩码 → 应用」是一条完整闭环，而不是改了却无处生效。
            string selection = string.IsNullOrWhiteSpace(row.Selection) ? row.Label : row.Selection;

            // 掩码与优先级都可能被改过，两者都要落盘 —— 只判断掩码的话，
            // 「只改了优先级 → 点应用」会走进死胡同：界面显示改了，文件里还是旧的。
            bool maskChanged = !string.Equals(selection, row.Label, StringComparison.Ordinal);
            bool priorityChanged = !string.Equals(row.PrioritySelection, row.Priority, StringComparison.Ordinal);

            if (!maskChanged && priorityChanged)
            {
                // 只改优先级：掩码部分要保持原样，走空串让 UpsertRule 不动 Target。
                // 这里必须把原有的 target 传进去，不能传 None（None 会被当成删除规则）。
                BindingRule? keep = BindingStore.LookupByName(row.Name);
                if (keep != null)
                {
                    UpsertRule(row.Name, keep.Target, keep.MaskId, keep.Mask, row.Path, row.PrioritySelection);
                    ReloadRulesAfterChange();
                }
            }

            if (maskChanged)
            {
                (BindingTarget newTarget, string newMaskId, ulong newMask) = SelectionToTarget(selection);

                // 选到「不绑定」＝删除这条规则，给出明确提示而不是静默删掉
                if (newTarget == BindingTarget.None)
                {
                    RemoveRuleBySelection(row);
                    return;
                }

                UpsertRule(row.Name, newTarget, newMaskId, newMask, row.Path, row.PrioritySelection);
                ReloadRulesAfterChange();
            }

            BindingRule? rule = BindingStore.LookupByName(row.Name);
            if (rule == null)
            {
                ToastService.Warning("该规则已不存在");
                return;
            }

            if (rule.Target == BindingTarget.None)
            {
                ToastService.Warning("该规则没有设置绑定目标");
                return;
            }

            // 上方「运行中进程」的同名下拉框同步到最新值，避免两处显示不一致
            FindGroup(row.Name)?.SetInitialSelection(selection);

            // 优先级与绑核一起下发：一条规则 = 一套完整调度，
            // 只恢复掩码不恢复优先级，用户会觉得「改了 High 怎么又变回 Normal」。
            int priorityOk = 0;
            if (!string.IsNullOrEmpty(rule.Priority))
            {
                ProcessGroupRow? live = FindGroup(row.Name);
                if (live != null)
                    priorityOk = ApplyPriority(live.Members, rule.Priority).Ok;
            }

            int bound = HotApplyRule(row.Name);

            // 提示里带上优先级，否则用户看不出「应用」到底做了两件事还是一件
            string prioNote = priorityOk > 0 ? $"，优先级 {rule.Priority}" : string.Empty;

            string msg = bound > 0
                ? $"已重新应用规则：{row.Name} → {rule.Label}{prioNote}，对 {bound} 个正在运行的进程生效。"
                : $"规则已就绪：{row.Name} → {rule.Label}{prioNote}。当前该进程没有正在运行的实例，启动后会自动生效。";

            LogService.Info(msg);
            ToastService.Success(msg);
        }

        /// <summary>规则行选了「不绑定」：删掉这条规则，并把界面上的下拉框复位。</summary>
        private void RemoveRuleBySelection(SavedRuleRow row)
        {
            if (!DarkDialog.Confirm(
                    $"「{row.Name}」已选择「不绑定」，应用后将删除这条规则。是否继续？",
                    "应用规则"))
                return;

            DeleteRule(row.Name);
            LogService.Info($"已按界面选择解除绑定：{row.Name}");
        }

        /// <summary>单条规则「删除」：只删这一条，其余规则保留。</summary>
        private void DeleteSingleRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: SavedRuleRow row })
                return;

            if (!DarkDialog.Confirm($"确定要删除规则「{row.Name} → {row.Label}」吗？", "删除规则"))
                return;

            DeleteRule(row.Name);
            LogService.Info($"已删除单条规则：{row.Name}");
        }

        /// <summary>删掉一条规则并复位界面上下方列表的同名下拉框。</summary>
        private void DeleteRule(string name)
        {
            List<BindingRule> rules = BindingStore.Profile.Rules;
            BindingRule? existing = BindingStore.LookupByName(name);

            if (existing != null)
            {
                rules.Remove(existing);
                BindingStore.Save(rules);
            }

            FindGroup(name)?.SetInitialSelection("不绑定");
            ReloadRulesAfterChange();
        }

        /// <summary>
        /// 「启用 / 禁用」按钮：翻转该条规则的启用状态。
        /// 之前这里是 ToggleSwitch 双向绑定，一点就生效、没有确认也没有提示；
        /// 现在改成显式按钮，动作、落盘、刷新文案都在这一处完成。
        /// </summary>
        private void ToggleRuleEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: SavedRuleRow row })
                return;

            row.Enabled = !row.Enabled;   // setter 内部触发 OnRuleEnabledChanged 落盘
            row.NotifyEnabledChanged();    // 刷新按钮文案与提示

            // 按钮显示的是"点了会发生什么"：已启用时显示「禁用」。
            // 启用/禁用的结果提示：纯状态回执，改 Toast。
            // 真正的破坏性动作（删除规则、清空全部）仍走 Confirm 模态。
            ToastService.Success(
                row.Enabled
                    ? $"已启用规则：{row.Name} → {row.Label}"
                    : $"已禁用规则：{row.Name}（记录保留）");
        }

        /// <summary>规则开关变化：写回 bindings.json，并让监听立即按新状态重扫。</summary>
        private void OnRuleEnabledChanged(SavedRuleRow? row)
        {
            if (row == null || _loadingRules)
                return;

            BindingRule? rule = BindingStore.LookupByName(row.Name);
            if (rule == null)
                return;

            rule.Enabled = row.Enabled;
            BindingStore.Save(BindingStore.Profile.Rules);
            // 清掉「已处理」缓存：重新启用后可立即对已在运行的实例补绑
            ProcessWatcher.ResetCache();

            if (row.Enabled)
            {
                // 热绑定：开关一打开就立刻对正在运行的实例下发，不等后台轮询
                // 优先级与绑核一起下发：一条规则 = 一套完整调度，
            // 只恢复掩码不恢复优先级，用户会觉得「改了 High 怎么又变回 Normal」。
            int priorityOk = 0;
            if (!string.IsNullOrEmpty(rule.Priority))
            {
                ProcessGroupRow? live = FindGroup(row.Name);
                if (live != null)
                    priorityOk = ApplyPriority(live.Members, rule.Priority).Ok;
            }

            int bound = HotApplyRule(row.Name);
                LogService.Info(bound > 0
                    ? $"规则已启用：{row.Name} → {row.Label}，并对 {bound} 个正在运行的进程立即生效"
                    : $"规则已启用：{row.Name} → {row.Label}（当前无运行实例，启动后自动生效）");
            }
            else
            {
                LogService.Info($"规则已停用：{row.Name}（记录保留，后台不再自动绑定）");
            }
        }
    }
}
