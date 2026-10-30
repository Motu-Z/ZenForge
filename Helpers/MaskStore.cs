using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ZenForge.Services;

namespace ZenForge.Helpers
{
    /// <summary>一条用户自定义的 CPU 核心掩码配置。</summary>
    public sealed class CustomMask
    {
        /// <summary>稳定 ID（用于规则引用，删除/改名不影响引用）。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>用户自定义名称，如「纯大核」「CCD1 no SMT」。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>逻辑核掩码，位 i = 1 表示包含逻辑处理器 i。</summary>
        public ulong Mask { get; set; }

        /// <summary>已勾选的逻辑核数量（用于列表展示）。</summary>
        public int CoreCount => BitOperations.PopCount(Mask);
    }

    /// <summary>
    /// 自定义掩码持久化：保存到 %APPDATA%\ZenForge\masks.json。
    /// 重启软件时由 <see cref="Load"/> 自动加载，下拉框与绑核规则都引用这里的配置。
    /// </summary>
    public static class MaskStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZenForge", "masks.json");

        private static readonly List<CustomMask> _masks = new();

        public static IReadOnlyList<CustomMask> Masks => _masks;

        /// <summary>
        /// 自定义掩码套数上限。
        ///
        /// 主界面「调度」页的绑定目标下拉框是「不绑定 / CCD0 / CCD1 / 各自定义掩码」平铺，
        /// 掩码再多就是一条越滚越长的下拉列表，挑一条要看半天。10 套是够用又不失控的量。
        /// </summary>
        public const int MaxCount = 10;

        /// <summary>当前是否已经达到套数上限（新建前用它决定是否还允许加草稿）。</summary>
        public static bool IsFull => _masks.Count >= MaxCount;

        /// <summary>
        /// 除某个 Id 之外已占用的名称，供重名校验使用。
        ///
        /// 按 Id 排除而不是按名称排除 —— 草稿的名字可能刚好和某条已保存的掩码同名
        /// （用户手敲了一个「纯大核」），按名称排除会把真正的冲突对象一起跳过，
        /// 于是重名检查形同虚设。Id 是唯一可靠的「自己」标识。
        /// </summary>
        public static IEnumerable<string> NamesExceptId(string? excludeId)
        {
            foreach (CustomMask m in _masks)
            {
                if (excludeId != null && string.Equals(m.Id, excludeId, StringComparison.Ordinal))
                    continue;
                yield return m.Name;
            }
        }

        /// <summary>任一掩码被新增 / 改名 / 删除时触发（用于联动主界面下拉框）。</summary>
        public static event Action? Changed;

        public static void Load()
        {
            try
            {
                List<CustomMask>? loaded = JsonFile.Read<List<CustomMask>>(FilePath);
                if (loaded == null)
                    return;

                _masks.Clear();
                _masks.AddRange(loaded.Where(m => !string.IsNullOrWhiteSpace(m.Name)));
            }
            catch (Exception ex)
            {
                LogService.Error($"读取自定义掩码失败: {ex.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                JsonFile.Write(FilePath, _masks);
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                LogService.Error($"保存自定义掩码失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 掩码为何无法写入。
        /// </summary>
        public enum UpsertError
        {
            None = 0,
            /// <summary>名称为空或超出长度上限。</summary>
            BadName,
            /// <summary>与已有掩码重名（忽略大小写）。</summary>
            DuplicateName,
            /// <summary>已是第 11 套，超过 <see cref="MaxCount"/>。</summary>
            TooMany
        }

        /// <summary>
        /// 新增或覆盖一条掩码（按 Id 判断）。
        /// </summary>
        /// <returns>写入成功为 true；否则 false，具体原因见 <paramref name="error"/>。</returns>
        /// <remarks>
        /// 旧实现在这里对超长名称做静默 Substring 截断，绕过界面直接写入时用户
        /// 不会收到任何提示，名字被悄悄砍掉。改为拒绝写入，由调用方弹出提示。
        /// 上限口径统一取自 <see cref="MaskNamePolicy"/>。
        ///
        /// 重名与套数上限也放在这里，而不是只写在窗口里 —— 窗口是可以绕过的
        /// （以后加导入 / 迁移入口就会绕过去），存储层才是唯一可信的关卡。
        /// </remarks>
        public static bool Upsert(CustomMask mask, out UpsertError error)
        {
            string name = (mask.Name ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(name) ||
                MaskNamePolicy.Weight(name) > MaskNamePolicy.MaxWeight)
            {
                error = UpsertError.BadName;
                return false;
            }

            CustomMask? existing = _masks.FirstOrDefault(m => m.Id == mask.Id);

            // 新建才查数量上限；改已有的不会增加条数
            if (existing == null && _masks.Count >= MaxCount)
            {
                error = UpsertError.TooMany;
                return false;
            }

            foreach (CustomMask other in _masks)
            {
                if (ReferenceEquals(other, existing) || string.Equals(other.Id, mask.Id, StringComparison.Ordinal))
                    continue;

                if (string.Equals(other.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    error = UpsertError.DuplicateName;
                    return false;
                }
            }

            mask.Name = name;

            if (existing != null)
            {
                existing.Name = mask.Name;
                existing.Mask = mask.Mask;
            }
            else
            {
                _masks.Add(mask);
            }

            error = UpsertError.None;
            Save();
            return true;
        }

        public static void Remove(string id)
        {
            if (_masks.RemoveAll(m => m.Id == id) > 0)
                Save();
        }

        public static CustomMask? FindById(string id) => _masks.FirstOrDefault(m => m.Id == id);

        public static CustomMask? FindByName(string name) =>
            _masks.FirstOrDefault(m => m.Name == name);
    }
}
