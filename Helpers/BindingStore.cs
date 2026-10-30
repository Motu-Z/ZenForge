using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ZenForge.Helpers
{
    /// <summary>进程绑定目标。</summary>
    public enum BindingTarget
    {
        None = 0,
        CCD0 = 1,
        CCD1 = 2,

        /// <summary>自定义掩码：具体逻辑核集合由 <see cref="BindingRule.MaskId"/> 指向的掩码给出。</summary>
        Custom = 3
    }

    /// <summary>
    /// 单条绑核规则：进程名 → 目标 CCD / 自定义掩码。
    /// 规则按「进程名」（如 msedge.exe）记录，不再记录具体 PID 或路径，
    /// 这样同一程序的所有实例（PID）都会自动命中。
    /// </summary>
    public sealed class BindingRule
    {
        /// <summary>进程名（含 .exe，如 "msedge.exe"）。</summary>
        public string Name { get; set; } = string.Empty;

        public BindingTarget Target { get; set; } = BindingTarget.None;

        /// <summary>自定义掩码引用（Target = Custom 时有效），指向 <see cref="MaskStore"/> 中的 Id。</summary>
        public string MaskId { get; set; } = string.Empty;

        /// <summary>
        /// 自定义掩码的快照值（Target = Custom 时有效）。
        /// 用于掩码被删除后仍可按快照下发，提高后台监听的健壮性。
        /// </summary>
        public ulong Mask { get; set; }

        /// <summary>兼容旧版：迁移时由路径派生进程名。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 随规则一起保存的优先级档位（High / Above Normal / Normal / Below Normal / Low）。
        ///
        /// 空字符串表示「这条规则不碰优先级」，只有掩码调度 —— 旧配置文件没有这个字段，
        /// 反序列化后就是空串，行为与升级前完全一致。
        ///
        /// 为什么优先级要跟着规则存：只存掩码的话，用户把游戏设成 High + CCD0，
        /// 下次游戏启动时 ProcessWatcher 只恢复掩码，优先级又变回 Normal，
        /// 用户会以为「设置没保存」。跟着规则存才能让「一条规则 = 一套完整调度」成立。
        /// </summary>
        public string Priority { get; set; } = string.Empty;

        /// <summary>
        /// 该规则是否启用。关闭后记录仍然保留，但后台监听不会自动绑定，
        /// 适合「先存着、暂时不想生效」的场景。默认启用（旧配置文件缺此字段时也为启用）。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>规则的展示名（如 "CCD0"、"纯大核"、"0-7,16-23"）。</summary>
        public string Label
        {
            get
            {
                if (Target == BindingTarget.CCD0) return "CCD0";
                if (Target == BindingTarget.CCD1) return "CCD1";
                if (Target == BindingTarget.Custom)
                {
                    CustomMask? mask = MaskStore.FindById(MaskId);
                    return mask != null ? mask.Name : MaskFormatter.ToRangeText(Mask);
                }
                return "不绑定";
            }
        }
    }

    /// <summary>bindings.json 的根对象。</summary>
    public sealed class BindingProfile
    {
        public List<BindingRule> Rules { get; set; } = new();
    }

    /// <summary>
    /// 绑核规则持久化：保存到 %APPDATA%\ZenForge\bindings.json。
    /// 键为进程名（不再用路径），值为目标 CCD 或自定义掩码引用。
    /// </summary>
    public static class BindingStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZenForge", "bindings.json");

        /// <summary>
        /// 保护 Profile 引用与磁盘读写。
        /// 后台监听线程每轮 ScanOnce 都会 Reload()，而 UI 线程保存后调 Save()：
        /// 两者并发时，后完成的一方会把对方的改动整个覆盖掉（Save 用界面旧快照
        /// 覆盖磁盘新内容，或 Reload 把刚保存的 Profile 换成磁盘旧内容）。
        /// </summary>
        private static readonly object _sync = new();

        private static BindingProfile _profile = new();

        /// <summary>
        /// 当前规则集。这里返回的是**快照引用**而非拷贝：
        /// 读取方通常只做 Where/ToList 遍历，拷贝整份列表在每秒一次的轮询里没有必要。
        /// </summary>
        public static BindingProfile Profile
        {
            get { lock (_sync) return _profile; }
        }

        public static void Load()
        {
            lock (_sync)
            {
                try
                {
                    _profile = ReadFromDisk() ?? new BindingProfile();
                }
                catch
                {
                    _profile = new BindingProfile();
                }
            }
        }

        /// <summary>
        /// 读盘并做旧版迁移。文件不存在或反序列化出 null 时返回 null，
        /// 让调用方自己决定「落成空档」还是「保持现状」。
        /// </summary>
        private static BindingProfile? ReadFromDisk()
        {
            BindingProfile? loaded = JsonFile.Read<BindingProfile>(FilePath);
            if (loaded == null)
                return null;

            MigrateLegacyNames(loaded);
            return loaded;
        }

        /// <summary>旧版按路径存储：Name 为空但有 Path 时取文件名作为进程名。</summary>
        private static void MigrateLegacyNames(BindingProfile profile)
        {
            foreach (BindingRule rule in profile.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Name) &&
                    !string.IsNullOrWhiteSpace(rule.Path))
                {
                    rule.Name = System.IO.Path.GetFileName(rule.Path);
                }
            }
        }

        /// <summary>用给定规则集合覆盖并落盘（原子替换）。</summary>
        public static void Save(IEnumerable<BindingRule> rules)
        {
            BindingProfile profile = new() { Rules = new List<BindingRule>(rules) };
            lock (_sync)
            {
                try
                {
                    JsonFile.Write(FilePath, profile);
                    _profile = profile;
                }
                catch (Exception ex)
                {
                    Services.LogService.Error($"保存绑核规则失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 恢复出厂：清空全部绑核规则。
        ///
        /// 只清规则，不动进程页的掩码 —— 两者是独立概念，
        /// 一个管「哪个进程绑到哪」，一个管「绑的区域长什么样」。
        /// 掩码由 <see cref="MaskStore.ResetToFactory"/> 单独清。
        /// </summary>
        public static void ResetToFactory()
        {
            if (Profile.Rules.Count == 0)
                return;

            Save(Array.Empty<BindingRule>());
            Services.LogService.Info("全局重置：绑核规则已清空");
        }

        /// <summary>
        /// 从磁盘重新读取规则（供后台监听在每轮扫描前调用，让界面改动立即生效）。
        /// 读取失败或文件不存在时保留现有内存规则。
        /// </summary>
        public static void Reload()
        {
            lock (_sync)
            {
                try
                {
                    BindingProfile? loaded = ReadFromDisk();
                    if (loaded != null)
                        _profile = loaded;
                }
                catch
                {
                    // 读取失败：保持当前内存中的规则不变
                }
            }
        }

        /// <summary>
        /// 是否存在「会实际生效」的绑核规则。
        ///
        /// 用于判断后台监听值不值得常驻：规则全被关掉、目标为 None、或进程名为空时，
        /// 监听起来也只是空转，白占一份常驻内存。后台常驻的唯一价值是持续绑核，
        /// 没有可绑的规则，就没有常驻的理由。
        /// </summary>
        public static bool HasEnabledRules => Profile.Rules.Any(IsEffective);

        /// <summary>根据进程名查询绑定目标（无规则返回 null）。</summary>
        public static BindingRule? LookupByName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string normalized = name.Trim().ToLowerInvariant();
            return Profile.Rules.FirstOrDefault(rule =>
                !string.IsNullOrWhiteSpace(rule.Name) &&
                rule.Name.Trim().ToLowerInvariant() == normalized);
        }

        /// <summary>规则是否真会生效：启用、目标不是 None、进程名不为空，缺一不可。</summary>
        private static bool IsEffective(BindingRule rule) =>
            rule.Enabled &&
            rule.Target != BindingTarget.None &&
            !string.IsNullOrWhiteSpace(rule.Name);
    }
}
