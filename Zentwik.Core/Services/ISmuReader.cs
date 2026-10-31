using System;
using System.Collections.Generic;
using System.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// SMU 访问通道。实现方保证进程内只有一个底层 Cpu 实例，
    /// 且任何路径都不会 Dispose 它（ZenStates-Core 的 Opcode 是进程级静态资源，
    /// 关闭任意一个 Cpu 都会让其余实例的 CPUID / SMU 能力一起失效）。
    /// </summary>
    public interface ISmuReader : IDisposable
    {
        /// <summary>PawnIO 驱动与管理员权限是否就绪。</summary>
        bool IsAvailable { get; }

        string? LastError { get; }

        int CoreCount { get; }

        uint CoresPerCcd { get; }

        /// <summary>原始探测值；0 表示尚未探测到（与「真的读到 8」区分开）。</summary>
        uint CoresPerCcdRaw { get; }

        /// <summary>只读取已探测到的值，不触发探测。</summary>
        uint PeekCoresPerCcdRaw();

        /// <summary>执行一次完整的只读探测。</summary>
        SmuSnapshot ReadSnapshot();

        SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins);

        SmuFMaxResult WriteFMax(uint mhz);

        /// <summary>
        /// 等待驱动就绪，期间只重发测试消息、不重建实例。
        /// 返回 false 表示超时或被取消。
        /// </summary>
        bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default);
    }
}
