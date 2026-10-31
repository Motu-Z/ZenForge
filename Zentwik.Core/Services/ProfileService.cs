using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Zentwik.Core.Helpers;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 一份「方案」的完整快照：调校（CO / FMax）+ 调度（绑核规则 + 优先级）。
    ///
    /// 【为什么是一份快照，而不是三个 store 各自多套值】
    /// 用户心里的「游戏方案」是一整套东西：负压 -20、锁 4800MHz、把游戏绑到 CCD0。
    /// 切换方案时他要的是**整套一起换**，不是分别去调三个地方。
    /// 如果让 TuningStore / BindingStore 各自存 N 套，切换就得保证两次写盘原子性 ——
    /// 中途崩一次就是「负压是新方案的、绑核还是旧方案的」，这种半套状态最难排查。
    /// 所以方案 = 一份完整快照，切换 = 一次性把三个 store 写回成快照里的值。
    ///
    /// 【刻意不进快照的三项（v1.1.x 后半段的扩展点）】
    /// 电源策略、升频模式、核心停放目前由 ProcessorPowerPolicy / ProcessorPowerTuning
    /// 直接读写电源方案与注册表，它们的「读回来做快照」语义还没定
    /// （电源方案是 GUID、升频模式是位域、核心停放是百分比，回滚时要不要先读旧值？）。
    /// 现在把字段定死，等于逼着 v1.1.x 为了兼容这些字段去改写那两个已经稳定的类。
    /// 这里留空，等那两个类提供 CurrentValue 快照接口后再加。
    /// </summary>
    public sealed class ProfileSnapshot : System.ComponentModel.INotifyPropertyChanged
    {
        /// <summary>方案 Id（32 位无横线 GUID）。改名不改 Id —— 场景规则引用的是 Id。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 这一份是否当前生效中。
        ///
        /// 【为什么放在数据模型上，而不是让界面自己记】
        /// 生效状态会被场景自动切换在后台改掉 —— 界面没参与那次切换，
        /// 自己记的那份立刻就过时了。放在模型上，由 ProfileService 统一刷新，
        /// 界面只需要一个 DataTrigger。
        ///
        /// 【为什么实现 INotifyPropertyChanged】
        /// ObservableCollection 只管集合的增删，不管元素属性变化。
        /// 切换方案时集合一条没动，变的只是哪个元素的 IsActive 为真；
        /// 不发属性通知，界面上的「生效中」标记就永远停在第一份方案上。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value)
                    return;

                _isActive = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        private bool _isActive;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = string.Empty;

        /// <summary>配置页列表里的那行描述。留空即不显示（界面不摆「无描述」占位）。</summary>
        public string Note { get; set; } = string.Empty;

        public string CreatedAt { get; set; } = DateTime.Now.ToString("o");

        public string UpdatedAt { get; set; } = DateTime.Now.ToString("o");

        /// <summary>每核 CO 值。空数组表示「这一套不碰 CO」（切换时不清零，保持现状）。</summary>
        public int[] CurveOptimizer { get; set; } = Array.Empty<int>();

        /// <summary>FMax 上限（MHz）。0 = 这一套不碰 FMax。</summary>
        public uint FMaxMHz { get; set; }

        /// <summary>
        /// 调度规则快照。存的是 <see cref="BindingRule"/> 的副本，
        /// 不是引用 —— 方案保存之后用户继续改进程页的规则，不能把已存的方案也改了。
        /// </summary>
        public List<BindingRule> BindingRules { get; set; } = new();
    }

    /// <summary>profiles.json 的根对象。</summary>
    public sealed class ProfileFile
    {
        public List<ProfileSnapshot> Profiles { get; set; } = new();

        /// <summary>当前生效方案的 Id。场景自动切换每次命中后也写这里。</summary>
        public string? ActiveId { get; set; }
    }

    /// <summary>
    /// 多方案的保存、切换、删除（v1.1.x 骨架）。
    ///
    /// 【这一层只做决策与落盘，硬件动作用现成的服务】
    /// 调校下发走 <see cref="TuningApplyService"/>（它已经封装了「写 SMU + 回读校验」），
    /// 绑核下发交给后台的 ProcessWatcher 按规则自然生效。
    /// 本类不碰任何 SMU / 注册表调用 —— 那是那两个服务的事，重复实现只会长出两套行为。
    /// </summary>
    public class ProfileService
    {
        private readonly ILogService _log;
        private readonly ITuningStore _tuning;
        private readonly IBindingStore _bindings;
        private readonly TuningApplyService _tuningApply;
        private readonly IProcessWatcher _watcher;
        private readonly IAppPaths _paths;

        public ProfileService(ILogService log, ITuningStore tuning, IBindingStore bindings, TuningApplyService tuningApply, IProcessWatcher watcher, IAppPaths paths)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            ArgumentNullException.ThrowIfNull(tuning);
            _tuning = tuning;
            ArgumentNullException.ThrowIfNull(bindings);
            _bindings = bindings;
            ArgumentNullException.ThrowIfNull(tuningApply);
            _tuningApply = tuningApply;
            ArgumentNullException.ThrowIfNull(watcher);
            _watcher = watcher;
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
        }

        private string FilePath => _paths.File("profiles.json");

        private readonly object Sync = new();

        /// <summary>界面绑定的集合。方案数量是个位数到几十，不需要虚拟化，但更新仍走增量。</summary>
        public ObservableCollection<ProfileSnapshot> Items { get; } = new();

        /// <summary>方案集合发生变化（增删改、切换生效）时触发，界面据此刷新。</summary>
        public event Action? Changed;

        /// <summary>当前生效方案的 Id；没有（启动后未切换过任何方案）时为 null。</summary>
        public string? ActiveId { get; private set; }

        /// <summary>
        /// 把生效标记刷到每项上。集合本身不变，只改元素的 IsActive。
        /// 每次写 ActiveId 之后都要调一次，否则界面上的标记会停在旧的那一套。
        /// </summary>
        private void RefreshActiveMarks()
        {
            foreach (ProfileSnapshot snapshot in Items)
                snapshot.IsActive = snapshot.Id == ActiveId;
        }

        /// <summary>程序启动、或 store 被别处改动后重新读盘。</summary>
        public void Load()
        {
            lock (Sync)
            {
                ProfileFile? file = null;
                try
                {
                    file = JsonFile.Read(FilePath, AppJsonContext.Default.ProfileFile);
                }
                catch (Exception ex)
                {
                    _log.Error($"读取方案列表失败: {ex.Message}");
                }

                List<ProfileSnapshot> loaded = file?.Profiles ?? new List<ProfileSnapshot>();
                ActiveId = file?.ActiveId;

                // 增量对齐而不是 Clear() 重建：Clear 会让列表重建全部可视容器，
                // 表现为一次闪烁 + 滚动弹回顶部（v1.0.3 在日志列表上修过同一个问题）。
                MergeInto(loaded);
                RefreshActiveMarks();
            }

            Changed?.Invoke();
        }

        /// <summary>按 Id 查方案。</summary>
        public ProfileSnapshot? FindById(string id)
        {
            lock (Sync)
                return Items.FirstOrDefault(p => p.Id == id);
        }

        /// <summary>
        /// 把「当前生效的设置」抓成一份新方案。
        ///
        /// 抓的是 store 里的值而不是界面上的临时输入：store 里存的是
        /// 「已成功下发并回读校验通过」的设定（见 TuningStore 的类注释），
        /// 界面上还没点应用的输入不配成为一份方案。
        /// </summary>
        public ProfileSnapshot CaptureCurrent(string name, string note = "")
        {
            TuningProfile tuning = _tuning.Profile;

            return new ProfileSnapshot
            {
                Name = name,
                Note = note,
                CurveOptimizer = (int[])tuning.CurveOptimizer.Clone(),
                FMaxMHz = tuning.FMaxMHz,

                // 规则也要克隆：BindingRule 是引用类型，直接塞列表会让方案
                // 与当前规则集共享同一批对象，之后改进程页等于改方案。
                BindingRules = _bindings.Profile.Rules
                    .Select(Clone)
                    .ToList()
            };
        }

        /// <summary>保存（新增或按 Id 覆盖）一份方案。</summary>
        public void Save(ProfileSnapshot snapshot)
        {
            snapshot.UpdatedAt = DateTime.Now.ToString("o");

            lock (Sync)
            {
                int index = Items.ToList().FindIndex(p => p.Id == snapshot.Id);
                if (index >= 0)
                    Items[index] = snapshot;
                else
                    Items.Add(snapshot);

                Persist();
            }

            _log.Info($"方案已保存: {snapshot.Name}");
            Changed?.Invoke();
        }

        /// <summary>删除一份方案。删的正好是生效中方案时只清标记，不动当前硬件设置。</summary>
        public bool Delete(string id)
        {
            lock (Sync)
            {
                ProfileSnapshot? target = Items.FirstOrDefault(p => p.Id == id);
                if (target == null)
                    return false;

                Items.Remove(target);

                // 刻意不回滚硬件：删掉一份方案只是不再留着这张卡片，
                // 用户没说「把机器恢复原样」—— 那是「清除全部数据」的职责。
                if (ActiveId == id)
                {
                    ActiveId = null;
                    RefreshActiveMarks();
                }

                Persist();
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 切换（应用）一份方案：把快照写回三个 store，再让现有服务下发。
        ///
        /// 【v1.1.x 仍待补的一处】
        /// 1. 重扫只做覆盖、不做解绑：不再命中任何规则的进程会保持上一次的绑核，
        ///    恢复默认需要显式写回系统默认 CPU Set，而那个「默认」在不同拓扑下
        ///    取值不同，没有实测依据之前不猜。
        /// 2. 失败回滚：调校下发失败时（TuningApplyOutcome.Success == false），
        ///    理想做法是恢复上一份方案。现在只记录日志，因为「部分写成功」的
        ///    回滚顺序（先 FMax 后 CO）还没有实测依据，不猜。
        /// </summary>
        public bool Apply(string id)
        {
            ProfileSnapshot? snapshot = FindById(id);
            if (snapshot == null)
                return false;

            // FMax 基线不进快照：它是这台机器的出厂值，属于硬件属性，
            // 不是方案属性 —— 换方案不该把基线也换掉。
            _tuning.Save(new TuningProfile
            {
                CurveOptimizer = (int[])snapshot.CurveOptimizer.Clone(),
                FMaxMHz = snapshot.FMaxMHz,
                FMaxBaselineMHz = _tuning.Profile.FMaxBaselineMHz
            });

            _bindings.Save(snapshot.BindingRules.Select(Clone));

            // 让**已经在跑**的进程立刻跟上新规则。
            // WMI 事件只覆盖之后启动的进程：不补这一下，用户切完方案看到的会是
            // 「设置都变了，但那个游戏还绑在旧核心上」，而它要等到下次重启才生效 ——
            // 那种「切了等于没切」的观感比不做这个功能更糟。
            // 重扫是异步的（内部 Task.Run），UI 线程不等它。
            _watcher.ReapplyNow();

            TuningApplyOutcome? outcome = _tuningApply.ApplyFromStore();
            if (outcome != null && !outcome.Success)
                _log.Warn($"方案「{snapshot.Name}」的调校部分未完全生效: {outcome.Summary}");

            lock (Sync)
            {
                ActiveId = snapshot.Id;
                RefreshActiveMarks();
                Persist();
            }

            _log.Info($"已切换方案: {snapshot.Name}");
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// 当前设置与给定方案是否一致。界面用它给「生效中」的那一张卡片打标，
        /// 而不是让用户记自己最后点的是哪一张。
        /// </summary>
        public bool IsCurrentEqualTo(string id)
        {
            ProfileSnapshot? snapshot = FindById(id);
            if (snapshot == null)
                return false;

            TuningProfile tuning = _tuning.Profile;
            if (!snapshot.CurveOptimizer.SequenceEqual(tuning.CurveOptimizer))
                return false;

            if (snapshot.FMaxMHz != tuning.FMaxMHz)
                return false;

            List<BindingRule> current = _bindings.Profile.Rules;
            if (current.Count != snapshot.BindingRules.Count)
                return false;

            // 只比「进程名 → 目标 + 优先级」三元组：Enabled 与 Mask 快照属于运行时状态，
            // 它们不一致不代表方案没生效（掩码被改名、规则被临时关掉都会造成差异）。
            return !current.Where((rule, i) =>
                !string.Equals(rule.Name, snapshot.BindingRules[i].Name, StringComparison.OrdinalIgnoreCase) ||
                rule.Target != snapshot.BindingRules[i].Target ||
                !string.Equals(rule.Priority, snapshot.BindingRules[i].Priority, StringComparison.Ordinal))
                .Any();
        }

        /// <summary>
        /// 把给定列表增量对齐到 <see cref="Items"/>。
        ///
        /// 【为什么不用 Clear + Add】
        /// Clear 会发一次 Reset 通知，列表重建全部可视容器：闪一下、滚动位置丢。
        /// 这里是配置页，方案只有几条，但规矩就是规矩 —— 顺手的地方最容易破例，
        /// 破了例下次在进程列表上也会这么写。
        /// </summary>
        private void MergeInto(List<ProfileSnapshot> desired)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!desired.Any(p => p.Id == Items[i].Id))
                    Items.RemoveAt(i);
            }

            for (int i = 0; i < desired.Count; i++)
            {
                int index = IndexOfId(desired[i].Id);
                if (index == -1)
                    Items.Insert(i, desired[i]);
                else if (index != i)
                    Items.Move(index, i);
            }
        }

        private int IndexOfId(string id)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id)
                    return i;
            return -1;
        }

        private void Persist()
        {
            try
            {
                JsonFile.Write(FilePath, new ProfileFile
                {
                    Profiles = Items.ToList(),
                    ActiveId = ActiveId
                }, AppJsonContext.Default.ProfileFile);
            }
            catch (Exception ex)
            {
                _log.Error($"保存方案列表失败: {ex.Message}");
            }
        }

        /// <summary>规则浅拷贝：方案里的规则必须与当前规则集是两批对象。</summary>
        private static BindingRule Clone(BindingRule rule) => new()
        {
            Name = rule.Name,
            Target = rule.Target,
            MaskId = rule.MaskId,
            Mask = rule.Mask,
            Path = rule.Path,
            Priority = rule.Priority,
            Enabled = rule.Enabled
        };
    }
}
