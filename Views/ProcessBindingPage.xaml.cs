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

        private bool _isSelected;

        /// <summary>
        /// 该行是否处于选中态。
        ///
        /// 【为什么必须做成绑定而不是直接改 Border.Background】
        /// 列表开启虚拟化 + Recycling 之后，滚出视野的行容器会被回收并交给
        /// 新出现的行复用。WPF 在复用容器时只会重新求值绑定，
        /// **不会**把上一行命令式设过的 Backgroud 清掉 ——
        /// 结果就是「选中色跟着滚动条跑」，滚到哪儿哪行是蓝的。
        ///
        /// 所以选中态必须是数据的一部分（本属性），由 XAML 的 DataTrigger
        /// 映射成底色；容器怎么复用都不会错。
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
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
        /// <summary>本页在路由中的 key，与 MainWindow 侧栏的 Tag 一致。</summary>
        public const string PageKey = "process";

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

        public const string PriorityHigh = ProcessPriority.High;
        public const string PriorityAboveNormal = ProcessPriority.AboveNormal;
        public const string PriorityNormal = ProcessPriority.Normal;
        public const string PriorityBelowNormal = ProcessPriority.BelowNormal;
        public const string PriorityLow = ProcessPriority.Low;

        /// <summary>优先级下拉的选项，顺序由高到低（与页面其他下拉一致）。</summary>
        public static ObservableCollection<string> PriorityOptions { get; } =
            new(ProcessPriority.Options);

        // 优先级的档位常量、下拉顺序与下发实现全部在 Services/ProcessPriority.cs。
        // 这里曾经自己实现过一份 OpenProcess + SetPriorityClass，与 ProcessWatcher
        // 里另一份重复，而且两份都漏了查询权限 —— 那就是「某些进程设不了优先级」的成因。
        // 页面现在只负责「取成员 PID → 调服务 → 报结果」。

        private readonly ObservableCollection<ProcessGroupRow> _groups = new();
        private readonly List<ProcessGroupRow> _allGroups = new();
        private readonly ObservableCollection<SavedRuleRow> _savedRules = new();

        /// <summary>可排序的列。核心掩码不在其中 —— 它是位掩码，没有大小关系。</summary>
        private enum SortColumn
        {
            Cpu,
            Name,
            Path,
            Mask,
            Priority,
        }

        /// <summary>当前排序列，默认按占用。</summary>
        private SortColumn _sortColumn = SortColumn.Cpu;

        /// <summary>当前是否降序。数值列默认降序（大的在前），名称/路径默认升序。</summary>
        private bool _sortDescending = true;

        /// <summary>规则列表重建期间抑制开关回调，避免误写盘。</summary>
        private bool _loadingRules;

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

            // 【搜索框只挂 TextChanged】
            // zenforge:SearchBox 内部已经处理了「占位符何时隐藏」「图标何时变色」
            // 「选中文案的透明度」三件事，页面只管过滤列表。
            // 旧版把这些全写在页面里：占位符的 Margin 用错了坐标系（详见
            // Themes/SearchBox.xaml 顶部注释），且 LostFocus 里读 IsKeyboardFocusWithin
            // 拿到的是旧值，于是占位符要么不消失、要么不回来。
            SearchBox.TextChanged += SearchBox_TextChanged;

            // 列头初态：默认为「占用 ▼」，这里统一刷一遍，避免 XAML 里写的
            // 初始文字与代码状态各写一份（改排序默认值时就只有一处要改）。
            UpdateSortHeaders();

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
                else
                {
                    // 采样器刚开始跑、首轮还没出结果：主动催一次。
                    // 不催的话要干等一个采样周期（5 秒），用户看到的是一个空列表 ——
                    // 这会让人以为「列表坏了、不刷新」。
                    RequestImmediateSample();
                }
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

        /// <summary>
        /// 采样线程推来一帧快照。
        ///
        /// 【这个方法是 async void，且由后台线程直接调用】
        /// 1.0.0-can8 之前它长这样：
        ///
        ///     List&lt;GroupedProcess&gt; grouped = await Task.Run(...);
        ///     ApplySnapshot(grouped);                     // ← 在哪个线程？
        ///
        /// 看起来 await 之后会「回到原线程」，但那个「原线程」是**采样器的线程池
        /// 线程**，不是 UI 线程 —— async 只保证回到调用它的那个 SynchronizationContext，
        /// 线程池线程上没有这个上下文。于是后半段依旧在后台线程执行，
        /// ApplySnapshot 去写绑定在 ItemsControl 上的 ObservableCollection，
        /// WPF 立刻抛「调用线程必须为 STA，因为许多 UI 组件都需要」。
        ///
        /// 日志里那一整屏 WARN 全部来自这里：采样本身是好的，数据也拿到了，
        /// 只是「往回送」的那一步走错了线程。同时 _snapshotBusy 在 finally 里
        /// 被清掉，于是下一轮又同样失败 —— 表现就是「列表永远不刷新」。
        ///
        /// 现在把「计算」和「回写」彻底分开：
        ///   计算在后台（纯 CPU：GroupEntries 只吃快照，不碰任何 UI 对象）；
        ///   回写一律 Dispatcher.BeginInvoke（也可用 Invoke，但采样线程不该等 UI 帧）。
        /// </summary>
        private void OnProcessesRefreshed(IReadOnlyList<ProcessRefresher.Entry> entries)
        {
            // 【不能在这里做「忙则丢弃」的判断】
            // 上一版在第一行就 Interlocked 抢占，一旦 Unowned 就 return；
            // 那个标志原本只该跳过「重复计算」，不该丢弃「唯一一次回写」。
            // 现在的语义是：计算可能被合并，但最新一帧一定会被送到 UI。
            if (System.Threading.Interlocked.Exchange(ref _snapshotBusy, 1) == 1)
            {
                // 已有一次在算：记下「又来了一帧」，算完后再补最后一帧，
                // 保证用户最终看到的一定是最新数据（而不是被丢弃的那一帧旧数据）。
                _snapshotDirty = true;
                return;
            }

            _ = PushSnapshotAsync(entries);
        }

        /// <summary>计算期间又来了新快照：算完补一帧。</summary>
        private volatile bool _snapshotDirty;

        private async Task PushSnapshotAsync(IReadOnlyList<ProcessRefresher.Entry> entries)
        {
            try
            {
                while (true)
                {
                    _snapshotDirty = false;

                    List<GroupedProcess> grouped = await Task.Run(() => GroupEntries(entries))
                        .ConfigureAwait(false);

                    // 【唯一正确的一跳：回到 UI 线程再动集合】
                    // 显式丢弃返回的 DispatcherOperation：这里不需要等它执行完，
                    // 也不该等（等在采样线程上会把下一帧采样一起堵住）。
                    _ = Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() =>
                        {
                            try
                            {
                                ApplySnapshot(grouped);
                            }
                            catch (Exception ex)
                            {
                                LogService.Warn($"进程列表刷新失败：{ex.Message}");
                            }
                        }));

                    if (!_snapshotDirty)
                        break;

                    // 有新帧进来：用最新的那份重算，别把旧数据画上去。
                    entries = ProcessRefresher.Latest;
                    if (entries.Count == 0)
                        break;
                }
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

        /// <summary>
        /// 主动催一次采样（进页面立刻要数据时用）。
        ///
        /// 采样线程本身会处理节流：这里只负责把「现在就要」这件事排进它的队列，
        /// 不在 UI 线程上同步跑 Collect —— 那会把 200+ 进程的 OpenProcess 压在 UI 上。
        /// </summary>
        private static void RequestImmediateSample()
        {
            try
            {
                ProcessRefresher.RequestSample();
            }
            catch (Exception ex)
            {
                LogService.Warn($"请求进程快照失败：{ex.Message}");
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

            // 排序规则见 SortRows。这里只负责「过滤 → 排序 → 增量同步」中的排序一步。
            List<ProcessGroupRow> desired = SortRows(rows).ToList();

            // 增量同步：原地增删 + 重排，避免每次刷新整列表重建
            // （Clear+Add 会让 ListView 重建设施，造成闪屏、滚动跳变、焦点丢失）。
            // 同一进程名的行对象在 _allGroups 中被复用，容器随之复用，无闪烁。
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

        // ==================== 排序 ====================

        /// <summary>
        /// 按当前排序列与方向排序。
        ///
        /// 【两条稳定性规则，缺一条列表就会「自己跳」】
        ///
        /// 1. 主键相同的行，必须再按名称定序。否则同值行的相对顺序由排序算法
        ///    的内部实现决定，每次采样都可能不同的排列 —— 用户看到的是几行
        ///    一模一样的数字在互相换位。
        ///
        /// 2. 占用率按**显示精度（0.1%）**取整后再比。列表里显示的是
        ///    "0.0%"，而真实值是 0.031 与 0.028：不取整的话，显示完全相同的
        ///    两行会随着这点误差来回翻。
        ///
        /// 反过来也要注意：取整不是为了让「排序变慢」，真实负载变化
        /// （5% → 40%）该换位就得换位。刷新的节奏由采样周期控制，不由排序控制。
        /// </summary>
        private IEnumerable<ProcessGroupRow> SortRows(IEnumerable<ProcessGroupRow> rows)
        {
            // 掩码名的名次表：一次建好，排序比较时只做查字典。
            Dictionary<string, int> maskRank = BuildMaskRank();

            return _sortColumn switch
            {
                SortColumn.Name => _sortDescending
                    ? rows.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),

                // 路径排序用系统默认的字符串比较（区分大小写、按码位）：
                // 路径是唯一值，不存在稳定性问题，按字面排就够了。
                SortColumn.Path => _sortDescending
                    ? rows.OrderByDescending(g => g.Path, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),

                // 掩码按「下拉里显示的顺序」排。这里必须在排序前把 rank 算好：
                // 比较器会被调用 O(n log n) 次，每次现查 IndexOf 就是白烧 CPU。
                //
                // 降序要先按「已知/未知」分组再排。直接 OrderByDescending(rank)
                // 会把未知掩码用的 int.MaxValue 顶到最前面 —— 已删除掩码的进程
                // 排在第一屏，是最糟的结果。
                SortColumn.Mask => _sortDescending
                    ? rows.OrderBy(g => IsUnknownMask(g, maskRank) ? 1 : 0)
                          .ThenByDescending(g => MaskRank(g, maskRank))
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(g => MaskRank(g, maskRank))
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),

                // 优先级的未知档位（驱动进程的 24 之类）同理：降序时也要压在最后，
                // 不能让「不认识」变成「最优先」。
                SortColumn.Priority => _sortDescending
                    ? rows.OrderBy(g => IsUnknownPriority(g) ? 1 : 0)
                          .ThenByDescending(PriorityRank)
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(PriorityRank)
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),

                _ => _sortDescending
                    ? rows.OrderByDescending(g => Math.Round(g.CpuValue, 1))
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(g => Math.Round(g.CpuValue, 1))
                          .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            };
        }

        /// <summary>
        /// 掩码名 → 显示顺序（0 = 不绑定）。
        ///
        /// 顺序直接取 <see cref="TargetOptions"/> 的下标，也就是下拉框里从上到下
        /// 看到的顺序：不绑定 / CCD0 / CCD1 / 各自定义掩码（用户自己的排列）。
        /// 不用字母序：字母序会把「CCD0」「不绑定」和自定义名混在一起，
        /// 用户眼里的「同类」被拆散，等于没排。
        /// </summary>
        private static Dictionary<string, int> BuildMaskRank()
        {
            Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < TargetOptions.Count; i++)
                rank[TargetOptions[i]] = i;
            return rank;
        }

        /// <summary>掩码名在显示顺序里的名次。不在表里的（已删除的掩码）排最后。</summary>
        private static int MaskRank(ProcessGroupRow row, Dictionary<string, int> rank)
            => rank.TryGetValue(row.Selection ?? string.Empty, out int index)
                ? index
                : int.MaxValue;

        /// <summary>该行的掩码名不在当前选项表里（掩码已被删除 / 名字被改过）。</summary>
        private static bool IsUnknownMask(ProcessGroupRow row, Dictionary<string, int> rank)
            => !rank.ContainsKey(row.Selection ?? string.Empty);

        /// <summary>该行的优先级不等于任何一个标准档位。</summary>
        private static bool IsUnknownPriority(ProcessGroupRow row)
            => Array.IndexOf(ProcessPriority.Options, row.PrioritySelection) < 0;

        /// <summary>
        /// 优先级档位 → 排序序号（0 = 最高）。
        ///
        /// 用下拉选项表的下标而不是 SetPriorityClass 的常量值：那些常量
        /// （0x80 / 0x8000 / 0x20 …）是位标志，数值大小与「谁更优先」毫无关系，
        /// 直接按常量排会把 Low(0x40) 排到 Normal(0x20) 前面。
        ///
        /// 不认识的档位（驱动的 24、High 的变体）统一排到末尾，而不是当成 0
        /// 混进「高」里 —— 那些进程本来就不在任何标准档上，放在最末尾更诚实。
        /// </summary>
        private static int PriorityRank(ProcessGroupRow row)
        {
            int index = Array.IndexOf(ProcessPriority.Options, row.PrioritySelection);
            return index < 0 ? ProcessPriority.Options.Length : index;
        }

        /// <summary>
        /// 点击列头：同列切方向，异列切到该列的默认方向。
        ///
        /// 默认方向按「用户点这一列时想先看什么」定：数值列（占用、优先级）
        /// 先给降序（大的在前），文本列（名称、路径）先给升序（A 在前）。
        /// </summary>
        private void SortHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBlock { Tag: string tag })
                return;

            SortColumn column = tag switch
            {
                "Name" => SortColumn.Name,
                "Path" => SortColumn.Path,
                "Mask" => SortColumn.Mask,
                "Priority" => SortColumn.Priority,
                _ => SortColumn.Cpu,
            };

            if (column == _sortColumn)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sortColumn = column;
                // 数值列先给降序（大的在前）；名称/路径/掩码先给升序
                // —— 掩码的升序就是下拉里的顺序，读起来是「不绑定在最上面」。
                _sortDescending = column is SortColumn.Cpu or SortColumn.Priority;
            }

            UpdateSortHeaders();

            // 用户主动排序，立即生效：不走「正在操作就先推迟」的让位逻辑。
            ApplyFilter(true);
            e.Handled = true;
        }

        /// <summary>
        /// 把当前排序状态画到列头上：排序列带箭头且用主文字色，其余列用元数据色。
        ///
        /// 颜色必须用 SetResourceReference 而不是直接赋 Brush：主题切换时
        /// 资源引用会自动重取，直接赋值的画刷会停在切换前那一版。
        /// </summary>
        private void UpdateSortHeaders()
        {
            Apply(NameHeaderText, "进程名", SortColumn.Name);
            Apply(PathHeaderText, "路径", SortColumn.Path);
            Apply(CpuHeaderText, "占用", SortColumn.Cpu);
            Apply(MaskHeaderText, "核心掩码", SortColumn.Mask);
            Apply(PriorityHeaderText, "优先级", SortColumn.Priority);

            void Apply(TextBlock? header, string title, SortColumn column)
            {
                if (header == null)
                    return;

                bool active = _sortColumn == column;
                header.Text = active ? title + (_sortDescending ? " ▼" : " ▲") : title;
                header.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    active ? "Text.Primary" : "Text.MetaBrush");

                header.ToolTip = active
                    ? $"点击切换排序方向（当前：{(_sortDescending ? "从高到低" : "从低到高")}）"
                    : $"点击按{title}排序";
            }
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
        /// 点击进程行：选中该行（v12 的 process-row.active 视觉）。
        ///
        /// 自绘行没有 ListBox 的选中态，这里自己记一个选中行对象，
        /// 再把该行的 Border 底色改成强调色淡底。只做视觉反馈，
        /// 不改变任何绑定语义 —— 点行不等于改掩码，改掩码只有下拉框能做。
        /// </summary>
        private void ProcessRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not ProcessGroupRow clicked)
                return;

            // 点在下拉框上时不要抢走它自己的点击
            if (e.OriginalSource is DependencyObject src && FindAncestor<ComboBox>(src) != null)
                return;

            // 只改数据，不改视觉：底色由行模板里的 DataTrigger 绑 IsSelected 决定。
            // 直接改 Border.Background 在虚拟化回收下会把选中色带到别的行上（见 IsSelected 的注释）。
            foreach (ProcessGroupRow row in _allGroups)
            {
                bool shouldSelect = ReferenceEquals(row, clicked);
                if (row.IsSelected != shouldSelect)
                    row.IsSelected = shouldSelect;
            }
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

        /// <summary>
        /// pcPriClassBase → 可读标签。
        ///
        /// 这是「基础优先级类」，取值只有几档固定值（4/6/8/10/13/24/32），
        /// 不随线程动态优先级浮动。未知值原样带出，不猜。
        /// </summary>
        private static string PriorityLabel(int pcPriClassBase) =>
            ProcessPriority.FromBasePriority(pcPriClassBase);

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

        /// <summary>
        /// 搜索词变化。
        ///
        /// 占位符与图标状态由 zenforge:SearchBox 自己在内部处理，
        /// 这里只负责「过滤列表」这一件事 —— 职责分开之后，
        /// 就不会再出现「页面以为自己在管占位符、控件以为页面在管」的空白地带。
        /// </summary>
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 打字是用户主动行为，过滤必须立刻跟上，不能被交互保护推迟
            ApplyFilter(true);
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

            PriorityApplyResult result = ProcessPriority.Apply(
                group.Members.Select(m => m.Pid), group.PrioritySelection);

            if (result.Ok == 0 && result.Failed == 0 && string.IsNullOrEmpty(result.Error))
                return;

            if (result.Ok == 0)
            {
                // 全部失败。三种情况分开说，因为用户该做的事完全不同：
                //   · 进程刚好退出 → 什么都不用做，下一轮刷新它就没了；
                //   · 保护拒绝     → 这个进程改不了，说明白原因，不劝用户重试；
                //   · 其它         → 带上错误码，用户能拿去查。
                // 【不再写死「权限不足或进程已退出」】
                // 之前那句话把两种截然不同的失败混成一句猜测，用户看不出到底哪种，
                // 也没法判断要不要以管理员身份重跑。现在给出真实的错误码。
                if (ProcessPriority.IsTransient(result))
                {
                    LogService.Info($"设置优先级跳过：{group.Name}（进程已退出）");
                    ToastService.Info($"{group.Name} 已经退出，无需设置优先级");
                }
                else
                {
                    LogService.Warn($"设置优先级失败：{group.Name}（{result.Failed} 个实例）{result.Error}");
                    ToastService.Error($"无法修改 {group.Name} 的优先级：{result.Error}");
                }
                return;
            }

            LogService.Info($"优先级已设置：{group.Name} → {group.PrioritySelection}（{result.Ok} 个实例）");

            if (result.Failed > 0)
                ToastService.Info($"{group.Name} → {group.PrioritySelection}：{result.Ok} 个成功，{result.Failed} 个失败");
            else
                ToastService.Success($"{group.Name} → {group.PrioritySelection}");

            // 有保存规则的进程，把优先级一并记进规则，新实例启动时会被 ProcessWatcher 应用。
            if (_savedRules.Any(r => string.Equals(r.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            {
                (BindingTarget target, string maskId, ulong mask) = SelectionToTarget(group.Selection);
                UpsertRule(group.Name, target, maskId, mask, group.Path, group.PrioritySelection);
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

            RuleCountText.Text = $"{_savedRules.Count} 条";
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
        private static string PriorityLabelToOption(string label) =>
            ProcessPriority.FromBasePriority(label switch
            {
                "Idle" => 4,
                "Below Normal" => 6,
                "Normal" => 8,
                "Above Normal" => 10,
                "High" => 13,
                _ => 0,
            });

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
            string priorityError = string.Empty;
            if (!string.IsNullOrEmpty(rule.Priority))
            {
                ProcessGroupRow? live = FindGroup(row.Name);
                if (live != null)
                {
                    PriorityApplyResult pr = ProcessPriority.Apply(
                        live.Members.Select(m => m.Pid), rule.Priority);
                    priorityOk = pr.Ok;
                    if (pr.Ok == 0 && !ProcessPriority.IsTransient(pr))
                        priorityError = pr.Error;
                }
            }

            int bound = HotApplyRule(row.Name);

            // 提示里带上优先级，否则用户看不出「应用」到底做了两件事还是一件
            string prioNote = priorityOk > 0 ? $"，优先级 {rule.Priority}" : string.Empty;

            string msg = bound > 0
                ? $"已重新应用规则：{row.Name} → {rule.Label}{prioNote}，对 {bound} 个正在运行的进程生效。"
                : $"规则已就绪：{row.Name} → {rule.Label}{prioNote}。当前该进程没有正在运行的实例，启动后会自动生效。";

            LogService.Info(msg);
            ToastService.Success(msg);

            // 优先级没设上时必须单独说清楚，否则用户以为「应用」把两件事都做了
            if (!string.IsNullOrEmpty(priorityError))
                ToastService.Error($"优先级未能设置：{priorityError}");
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
                // 热绑定：开关一打开就立刻对正在运行的实例下发，不等后台轮询。
                // 优先级与绑核一起下发：一条规则 = 一套完整调度，
                // 只恢复掩码不恢复优先级，用户会觉得「改了 High 怎么又变回 Normal」。
                if (!string.IsNullOrEmpty(rule.Priority))
                {
                    ProcessGroupRow? live = FindGroup(row.Name);
                    if (live != null)
                    {
                        PriorityApplyResult pr = ProcessPriority.Apply(
                            live.Members.Select(m => m.Pid), rule.Priority);
                        if (pr.Ok == 0 && !ProcessPriority.IsTransient(pr))
                            ToastService.Error($"规则已启用，但优先级未能设置：{pr.Error}");
                    }
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
