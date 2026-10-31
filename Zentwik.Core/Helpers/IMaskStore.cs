using System;
using System.Collections.Generic;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 自定义核心掩码的持久化与访问。
    ///
    /// 实现方保证 <see cref="Masks"/> 返回的是内部列表本身（不拷贝），
    /// 因此调用方只应读取，不要自行修改返回的集合。
    /// </summary>
    public interface IMaskStore
    {
        /// <summary>当前全部自定义掩码。</summary>
        IReadOnlyList<CustomMask> Masks { get; }

        /// <summary>是否已达套数上限。</summary>
        bool IsFull { get; }

        /// <summary>掩码被新增、改名或删除时触发。</summary>
        event Action? Changed;

        /// <summary>除指定 Id 之外已占用的名称，供重名校验使用。</summary>
        IEnumerable<string> NamesExceptId(string? excludeId);

        /// <summary>从磁盘加载。文件不存在时保持当前内存状态。</summary>
        void Load();

        /// <summary>清空全部自定义掩码并落盘。</summary>
        void ResetToFactory();

        /// <summary>新增或按 Id 覆盖一条掩码。</summary>
        bool Upsert(CustomMask mask, out MaskStore.UpsertError error);

        void Remove(string id);

        CustomMask? FindById(string id);

        CustomMask? FindByName(string name);
    }
}
