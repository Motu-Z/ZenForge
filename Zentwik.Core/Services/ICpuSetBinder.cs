namespace Zentwik.Core.Services
{
    /// <summary>
    /// CPU Sets 绑定。实现方缓存逻辑处理器拓扑，首次访问时探测。
    /// </summary>
    public interface ICpuSetBinder
    {
        /// <summary>最近一次拓扑探测失败的原因；成功时为 null。</summary>
        string? LastTopologyError { get; }

        /// <summary>逻辑处理器数量（未探测到时为 0）。</summary>
        int LogicalProcessorCount { get; }

        /// <summary>丢弃已缓存的拓扑，下次访问重新探测。</summary>
        void ResetTopology();

        CpuSetBindResult BindToMask(int processId, ulong mask);

        CpuSetBindResult BindToCcd(int processId, int ccdIndex);

        bool ClearDefaultCpuSets(int processId, out string? error);

        bool IsCcdAvailable(int ccdIndex);
    }
}
