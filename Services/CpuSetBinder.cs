using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZenForge.Services
{
    /// <summary>一次绑核尝试的结果。</summary>
    public sealed class CpuSetBindResult
    {
        public bool Success { get; init; }

        /// <summary>失败原因（Success=true 时为 null）。</summary>
        public string? Error { get; init; }

        /// <summary>实际绑定的 CPU Set 个数。</summary>
        public int AppliedCount { get; init; }

        /// <summary>目标 CCD（绑核失败时也保留，便于日志）。</summary>
        public int CcdIndex { get; init; }
    }

    /// <summary>
    /// 基于 Windows CPU Sets（SetProcessDefaultCpuSets）的进程绑核封装。
    ///
    /// 为什么用 CPU Sets 而不是 SetProcessAffinityMask：
    ///   在 >64 逻辑处理器的系统上 AffinityMask 是 64 位位图，无法覆盖全部核心；
    ///   CPU Sets 是 Win10 1709+ 引入的现代接口，按 CpuSetId 数组工作，天然支持任意核数，
    ///   并且允许进程被调度器更智能地放置（不会被硬拴导致线程饥饿）。
    ///
    /// 本类只做“把进程的默认 CPU 集合限制到某个 CCD”这一件事：
    ///   - 只读当前系统 CpuSet 拓扑（GetSystemCpuSetInformation）
    ///   - 写目标进程的默认 CpuSet（SetProcessDefaultCpuSets）
    /// 不涉及任何 SMU / 电源 / VBIOS 逻辑。
    /// </summary>
    public static class CpuSetBinder
    {
        // ---------------- P/Invoke ----------------

        /// <summary>
        /// CPU_SET_INFORMATION_TYPE 的真实取值。
        ///
        /// winnt.h 中该枚举只有一个成员：
        ///     typedef enum _CPU_SET_INFORMATION_TYPE { CpuSetInformation } CPU_SET_INFORMATION_TYPE;
        /// 即恒等于 0，Windows 回填的每一条记录 Type 都是 0。
        ///
        /// 【历史 Bug】此处曾错误地按 Processor = 1 / NumaNode = 2 去判断，
        /// 导致所有条目都被跳过，拓扑恒为空，日志满屏「CPU Sets 枚举返回 0 条」。
        /// 这是纯解析逻辑错误，与管理员权限无关。
        /// </summary>
        private const ushort CpuSetInformationType = 0;

        /// <summary>
        /// SYSTEM_CPU_SET_INFORMATION（winnt.h）—— 严格按官方字节布局声明：
        ///   Size                     DWORD   @0
        ///   Type                     WORD    @4    ← 2 字节，其后有 2 字节填充
        ///   Id                       DWORD   @8    ← 联合体起点，4 字节对齐
        ///   Group                    WORD    @12
        ///   LogicalProcessorIndex    BYTE    @14
        ///   CoreIndex                BYTE    @15
        ///   LastLevelCacheIndex      BYTE    @16
        ///   NumaNodeIndex            BYTE    @17
        ///   EfficiencyClass          BYTE    @18
        ///   AllFlags                 BYTE    @19
        ///   Reserved                 DWORD64 @24   ← 8 字节对齐，20-23 为填充
        /// 整条记录官方长度为 32 字节。
        ///
        /// 注意两点：
        ///   1. Type 必须是 ushort（2 字节）。若写成 uint，Id/Group/LpIndex 会整体后移，
        ///      读出的 LogicalProcessorIndex 是垃圾值，CCD 分组随之全错。
        ///   2. Reserved 必须是 ulong（DWORD64）。写成 uint 虽不影响前 20 字节的读取，
        ///      但会让 Marshal.SizeOf 与实际条目长度不符，容易在别处埋坑。
        ///
        /// 条目推进统一使用 Windows 回填的 entry.Size（真实长度），不依赖 SizeOf。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SYSTEM_CPU_SET_INFORMATION
        {
            public uint Size;
            public ushort Type;
            public uint Id;
            public ushort Group;
            public byte LogicalProcessorIndex;
            public byte CoreIndex;
            public byte LastLevelCacheIndex;
            public byte NumaNodeIndex;
            public byte EfficiencyClass;
            public byte AllFlags;
            public ulong Reserved;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(
            IntPtr Information,
            uint BufferLength,
            out uint ReturnedLength,
            IntPtr Process,
            uint Flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(
            IntPtr Process,
            uint[] CpuSetIds,
            uint CpuSetIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// SetProcessDefaultCpuSets 官方要求 PROCESS_SET_LIMITED_INFORMATION。
        /// Win10 1607 及更早版本没有这个位，届时回退到 PROCESS_SET_INFORMATION。
        /// </summary>
        private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

        // ---------------- 拓扑缓存 ----------------

        /// <summary>逻辑处理器序号 → CpuSetId（按 CCD 分组）。</summary>
        private static List<uint>[]? _ccdCpuSetIds;

        /// <summary>按逻辑处理器序号（0 起）排列的 CpuSetId，供自定义掩码直接索引。</summary>
        private static uint[]? _lpToCpuSetId;

        private static int _ccdCount;

        private static readonly object _topologyLock = new();

        /// <summary>最近一次拓扑读取失败的原因（成功时为 null），供 UI 警告横幅展示。</summary>
        public static string? LastTopologyError { get; private set; }

        /// <summary>
        /// 拓扑是否可用。false 表示无法枚举 CPU Sets，此时所有绑定都会失败，
        /// UI 必须禁用绑定下拉框并显示红色警告，避免用户误以为绑定成功。
        /// </summary>
        public static bool IsTopologyAvailable
        {
            get
            {
                EnsureTopology();
                return _ccdCpuSetIds is { Length: > 0 } && _lpToCpuSetId is { Length: > 0 };
            }
        }

        /// <summary>清空拓扑缓存，下次访问时重新读取（供 UI「重试」或排障用）。</summary>
        public static void ResetTopology()
        {
            lock (_topologyLock)
            {
                _ccdCpuSetIds = null;
                _lpToCpuSetId = null;
                _ccdCount = 0;
                LastTopologyError = null;
            }
        }

        /// <summary>
        /// 当前系统的 CCD 数（无法判断时按物理核数推算，最少 1）。
        ///
        /// CpuSet 枚举失败时 _ccdCount 会是 0，直接用会让勾选界面
        /// 把所有核都归到 CCD0（标题出现「CCD0 · Core 0 - 15」这种错位）。
        /// 所以这里退化到「按物理核 / 8」推算——Zen 系列每 CCD 8 核。
        /// </summary>
        public static int CcdCount
        {
            get
            {
                EnsureTopology();

                if (_ccdCount > 0)
                    return _ccdCount;

                // 退化推算：物理核数 / 8，至少 1
                int logical = Environment.ProcessorCount;
                int physical = logical % 2 == 0 ? logical / 2 : logical;
                int inferred = physical / 8;

                return inferred >= 1 ? inferred : 1;
            }
        }

        /// <summary>
        /// 当前系统的逻辑处理器总数（无法读取时返回 0）。
        /// 供「自定义掩码」对话框决定要画多少个勾选框。
        ///
        /// 不依赖 CpuSet 枚举：GetSystemCpuSetInformation 在部分系统上
        /// 会因组/权限/驱动策略失败，但「有多少个逻辑处理器」这件事
        /// Environment.ProcessorCount 始终可靠。
        /// 勾选界面只需要核数，不需要 CpuSetId，所以这里优先用运行时值。
        /// </summary>
        public static int LogicalProcessorCount
        {
            get
            {
                EnsureTopology();

                // 优先 CpuSet 枚举结果（与实际可绑定的核严格一致）
                if (_lpToCpuSetId is { Length: > 0 })
                    return _lpToCpuSetId.Length;

                // 退化：CpuSet 不可用时用运行时核数（勾选界面仍可正常工作）
                int count = Environment.ProcessorCount;
                return count > 0 ? count : 0;
            }
        }

        // ---------------- 对外接口 ----------------

        /// <summary>
        /// 按逻辑核掩码把进程的默认 CPU 集合限制到指定核集合。
        ///
        /// 与 <see cref="BindToCcd"/> 的关系：CCD 绑定是掩码绑定的一种特例
        /// （CCD0 = 低 16 位全 1）。这里直接按位索引，支持任意组合，
        /// 例如 0x00FF00FF（0-7 与 16-23）、0x0000000F（仅前 4 核）。
        /// </summary>
        /// <param name="processId">目标进程 ID。</param>
        /// <param name="mask">逻辑核掩码，位 i = 1 表示包含逻辑处理器 i。</param>
        public static CpuSetBindResult BindToMask(int processId, ulong mask)
        {
            EnsureTopology();

            if (_lpToCpuSetId == null || _lpToCpuSetId.Length == 0)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = -1,
                    Error = "无法读取系统 CPU Set 拓扑"
                };
            }

            if (mask == 0)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = -1,
                    Error = "掩码为 0，没有可绑定的逻辑核"
                };
            }

            List<uint> ids = new();
            int available = _lpToCpuSetId.Length;

            for (int i = 0; i < 64 && i < available; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    ids.Add(_lpToCpuSetId[i]);
            }

            if (ids.Count == 0)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = -1,
                    Error = $"掩码 0x{mask:X} 未命中任何逻辑核（本机仅 {available} 个）"
                };
            }

            return ApplyCpuSets(processId, ids.ToArray(), -1);
        }

        /// <summary>
        /// 把指定进程的默认 CPU 集合限制到目标 CCD。
        /// </summary>
        /// <param name="processId">目标进程 ID。</param>
        /// <param name="ccdIndex">0 = CCD0，1 = CCD1。</param>
        public static CpuSetBindResult BindToCcd(int processId, int ccdIndex)
        {
            EnsureTopology();

            if (_ccdCpuSetIds == null || _ccdCount == 0)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = ccdIndex,
                    Error = "无法读取系统 CPU Set 拓扑"
                };
            }

            if (ccdIndex < 0 || ccdIndex >= _ccdCount)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = ccdIndex,
                    Error = $"CCD{ccdIndex} 不存在（本机仅 {_ccdCount} 个 CCD）"
                };
            }

            uint[] ids = _ccdCpuSetIds[ccdIndex].ToArray();
            if (ids.Length == 0)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = ccdIndex,
                    Error = $"CCD{ccdIndex} 未解析到任何 CPU Set"
                };
            }

            return ApplyCpuSets(processId, ids, ccdIndex);
        }

        /// <summary>
        /// 实际下发 CPU Set 的公共实现（CCD 绑定与掩码绑定共用）。
        /// </summary>
        private static CpuSetBindResult ApplyCpuSets(int processId, uint[] ids, int ccdIndex)
        {
            // 优先用 SET_LIMITED_INFORMATION（Win10 1709+ 官方要求），
            // 老系统不认这个位时回退到 SET_INFORMATION。
            IntPtr handle = OpenProcess(
                PROCESS_SET_LIMITED_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
            {
                handle = OpenProcess(
                    PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            }

            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = ccdIndex,
                    Error = $"OpenProcess 失败（错误 {err}，通常是权限不足或进程已退出）"
                };
            }

            try
            {
                bool ok = SetProcessDefaultCpuSets(handle, ids, (uint)ids.Length);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    return new CpuSetBindResult
                    {
                        Success = false,
                        CcdIndex = ccdIndex,
                        Error = $"SetProcessDefaultCpuSets 失败（错误 {err}）"
                    };
                }

                return new CpuSetBindResult
                {
                    Success = true,
                    CcdIndex = ccdIndex,
                    AppliedCount = ids.Length
                };
            }
            catch (Exception ex)
            {
                return new CpuSetBindResult
                {
                    Success = false,
                    CcdIndex = ccdIndex,
                    Error = ex.Message
                };
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// 判断 CCD 是否可用（用于 UI 提示与规则校验）。
        /// </summary>
        public static bool IsCcdAvailable(int ccdIndex)
        {
            EnsureTopology();
            return ccdIndex >= 0 && ccdIndex < _ccdCount;
        }

        // ---------------- 拓扑解析 ----------------

        /// <summary>
        /// 读取系统全部 Processor 类型 CpuSet，按 CCD 分组。
        ///
        /// 分组依据：AMD Zen 的 CCD 划分无法直接从 CpuSet 结构拿到，
        /// 这里采用「逻辑处理器序号均分」策略——先取同一 Group 内的 CpuSet 总数，
        /// 再按 CCD 数量等分。对 9955HX（16 核 / 32 线程 / 2 CCD）即为
        /// 逻辑核 0-15 → CCD0，16-31 → CCD1，与实际 SMU 拓扑一致。
        /// </summary>
        private static void EnsureTopology()
        {
            lock (_topologyLock)
            {
                // 必须两个数组都就绪才算加载完成。
                // 只判 _ccdCpuSetIds 会漏掉「扁平数组还没建」的情况——
                // 那样 LogicalProcessorCount 返回 0，自定义掩码窗口就只画得出 1 个核，
                // 正是「CCD0 · Core 0-0、只有一个 Core 0 T0」的来源。
                if (_ccdCpuSetIds != null && _lpToCpuSetId != null)
                    return;

                try
                {
                    List<(uint Id, byte LpIndex, ushort Group, byte LlcIndex)> processors = QueryProcessorCpuSets();
                    if (processors.Count == 0)
                    {
                        _ccdCount = 0;
                        _ccdCpuSetIds = null;
                        _lpToCpuSetId = null;
                        LastTopologyError ??= "CPU Sets 枚举返回 0 条（未解析到任何逻辑处理器）";
                        LogService.Error($"CPU Sets 拓扑不可用：{LastTopologyError}");
                        return;
                    }

                    int ccds = DetectCcdCount(processors.Count);
                    _ccdCount = ccds;

                    // 按 (Group, LogicalProcessorIndex) 排序，保证顺序稳定
                    processors.Sort((a, b) =>
                    {
                        int g = a.Group.CompareTo(b.Group);
                        return g != 0 ? g : a.LpIndex.CompareTo(b.LpIndex);
                    });

                    int perCcd = (processors.Count + ccds - 1) / ccds;

                    // 优先用 L3 缓存索引分组：AMD Zen 每个 CCD 独享一块 L3，
                    // 同一 CCD 内的所有逻辑核 LastLevelCacheIndex 相同，是天然且准确的 CCD 边界。
                    List<uint>[]? byCache = GroupByLastLevelCache(processors, ccds);
                    List<uint>[] groups;
                    string groupBasis;

                    if (byCache != null)
                    {
                        groups = byCache;
                        groupBasis = "L3 缓存索引（真实 CCD 边界）";
                    }
                    else
                    {
                        // 回退：均分。第 i 个处理器落入第 i / ceil(n / ccds) 组
                        groups = new List<uint>[ccds];
                        for (int i = 0; i < ccds; i++)
                            groups[i] = new List<uint>();

                        for (int i = 0; i < processors.Count; i++)
                        {
                            int group = Math.Min(i / perCcd, ccds - 1);
                            groups[group].Add(processors[i].Id);
                        }
                        groupBasis = $"逻辑序号均分（每组 {perCcd} 个）";
                    }

                    _ccdCpuSetIds = groups;

                    // 扁平数组：索引 = 排序后的逻辑处理器序号（0 起），值 = CpuSetId
                    uint[] flat = new uint[processors.Count];
                    for (int i = 0; i < processors.Count; i++)
                        flat[i] = processors[i].Id;
                    _lpToCpuSetId = flat;
                    LastTopologyError = null;

                    LogService.Info(
                        $"CPU Sets 拓扑：{processors.Count} 个逻辑处理器，划分为 {ccds} 个 CCD，分组依据 = {groupBasis}");
                }
                catch (Exception ex)
                {
                    _ccdCount = 0;
                    _ccdCpuSetIds = null;
                    _lpToCpuSetId = null;
                    LastTopologyError = $"CPU Sets 拓扑解析异常：{ex.Message}";
                    LogService.Error(LastTopologyError);
                }
            }
        }

        /// <summary>
        /// 按 LastLevelCacheIndex 分组（AMD Zen 每 CCD 独享 L3，缓存索引即 CCD 边界）。
        ///
        /// 仅在「分组数恰好等于推断出的 CCD 数」时才采用，否则返回 null 由调用方回退到均分，
        /// 避免某些平台把缓存索引报成全 0 或每核唯一，反而分出错误的组。
        /// </summary>
        private static List<uint>[]? GroupByLastLevelCache(
            List<(uint Id, byte LpIndex, ushort Group, byte LlcIndex)> processors, int ccds)
        {
            if (ccds <= 1)
                return null;

            List<byte> distinct = processors.Select(p => p.LlcIndex).Distinct().ToList();
            if (distinct.Count != ccds)
                return null;

            // 每个缓存组的大小必须一致（对称 CCD），否则说明这个索引不可信
            Dictionary<byte, List<uint>> buckets = new();
            foreach ((uint id, byte _, ushort _, byte llc) in processors)
            {
                if (!buckets.TryGetValue(llc, out List<uint>? list))
                    buckets[llc] = list = new List<uint>();
                list.Add(id);
            }

            int first = buckets[distinct[0]].Count;
            if (buckets.Values.Any(v => v.Count != first))
                return null;

            // 按组内最小逻辑处理器序号排序，保证 CCD0 在前（与逻辑核编号顺序一致）
            List<uint>[] groups = buckets
                .OrderBy(kv => processors
                    .Where(p => p.LlcIndex == kv.Key)
                    .Min(p => p.LpIndex))
                .Select(kv => kv.Value)
                .ToArray();

            return groups;
        }

        /// <summary>
        /// 推断 CCD 数量。
        ///
        /// 【为什么不能只看逻辑处理器数】
        /// 早先的实现是「物理核 ≥16 且能被 8 整除 → 每 CCD 8 核」，
        /// 这在 9955HX（16 核 / 2 CCD）上成立，但碰到 6 核 CCX 的 APU、
        /// 或 12 核 / 10 核这类非 8 整数倍的 SKU 就会算错：
        /// 算出的 CCD 分区与 SMU 侧的核位域对不上，
        /// 结果是「CCD0 绑定」和「CO 核 0-7」实际不是同一批核心——
        /// 界面看起来一切正常，写下去却错位。
        ///
        /// 现在优先用 SMU 读到的 coresPerCcx（与写入掩码同源），
        /// 拿不到时才退回按 8 核推断。
        /// </summary>
        private static int DetectCcdCount(int logicalProcessors)
        {
            // 逻辑处理器数为偶数，物理核 ≈ 逻辑 / 2
            int physical = logicalProcessors / 2 > 0 ? logicalProcessors / 2 : logicalProcessors;

            // 优先：SMU 报告的每 CCX 核数（0 表示未探测）。与 SmuReader.SplitCore 同一口径，
            // 保证「界面分区」与「实际写入的核位域」一致，这是本方法唯一可靠的依据。
            // 用 Raw 而不是 CoresPerCcd：后者未探测时会兜底成 8，
            // 拿兜底值当事实等于没探测也在猜，反而掩盖了「SMU 还没就绪」这件事。
            // 用 Peek 而不是 Shared：Shared 会「触发创建」，而本方法在应用启动、
            // 调度页首次加载时就会跑，那时 PawnIO 往往还没就绪。逼着此刻建实例
            // 会拿到一个 SMU 不应答的 Cpu 并占住单例，之后所有 SMU 读取都会失败。
            int perCcdFromSmu = (int)SmuReader.PeekCoresPerCcdRaw();
            if (perCcdFromSmu > 0 && physical >= perCcdFromSmu)
            {
                int n = physical / perCcdFromSmu;
                if (n >= 1) return n;
            }

            // 回退：Zen 5 桌面/移动常见 8 核/CCD
            if (physical >= 16 && physical % 8 == 0)
                return physical / 8;

            return 1;
        }

        /// <summary>调用 GetSystemCpuSetInformation 枚举全部 CPU Set 条目。</summary>
        private static List<(uint Id, byte LpIndex, ushort Group, byte LlcIndex)> QueryProcessorCpuSets()
        {
            List<(uint, byte, ushort, byte)> result = new();

            // ---------- 第一步：传 IntPtr.Zero 探测所需缓冲区大小 ----------
            // 按官方用法，此时函数返回 FALSE 且 lastError = ERROR_INSUFFICIENT_BUFFER(122)，
            // ReturnedLength 回填真实所需字节数。这一句是「能不能读到拓扑」的关键。
            uint required = 0;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out required, IntPtr.Zero, 0);

            const int MaxBufferSize = 4 * 1024 * 1024;
            int bufferSize = required > 0 ? (int)required + 1024 : 8192;

            IntPtr buffer = IntPtr.Zero;
            uint returned = 0;
            bool ok = false;

            try
            {
                // ---------- 第二步：按实际大小分配并真正读取 ----------
                for (int attempt = 0; attempt < 8 && bufferSize <= MaxBufferSize; attempt++)
                {
                    if (buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(buffer);

                    buffer = Marshal.AllocHGlobal(bufferSize);

                    ok = GetSystemCpuSetInformation(
                        buffer, (uint)bufferSize, out returned, IntPtr.Zero, 0);

                    if (ok)
                        break;

                    int err = Marshal.GetLastWin32Error();

                    // 缓冲区不够（122 / 111）→ 按回填值扩容重试
                    if (err == 122 || err == 111)
                    {
                        int need = returned > 0 ? (int)returned + 1024 : bufferSize * 2;
                        bufferSize = Math.Max(need, bufferSize * 2);
                        continue;
                    }

                    LastTopologyError = $"GetSystemCpuSetInformation 失败（错误 {err}）";
                    LogService.Error(LastTopologyError);
                    return result;
                }

                if (!ok)
                {
                    LastTopologyError = $"GetSystemCpuSetInformation 在 {bufferSize} 字节下仍失败";
                    LogService.Error(LastTopologyError);
                    return result;
                }

                // ---------- 第三步：逐条解析 ----------
                int offset = 0;
                int total = (int)returned;

                while (offset + 8 <= total)
                {
                    IntPtr entryPtr = buffer + offset;

                    // Size 是 DWORD(@0)，Type 是 WORD(@4)，必须按 2 字节读
                    uint size = (uint)Marshal.ReadInt32(entryPtr, 0);
                    ushort type = (ushort)Marshal.ReadInt16(entryPtr, 4);

                    if (size < 8)
                        break;

                    // CPU_SET_INFORMATION_TYPE 恒为 0（CpuSetInformation）。
                    // 之前误按 Processor = 1 匹配，导致条目全被丢弃——这是本次的根因。
                    if (type == CpuSetInformationType && size >= 24)
                    {
                        SYSTEM_CPU_SET_INFORMATION info =
                            Marshal.PtrToStructure<SYSTEM_CPU_SET_INFORMATION>(entryPtr);
                        result.Add((info.Id, info.LogicalProcessorIndex, info.Group, info.LastLevelCacheIndex));
                    }

                    offset += (int)size;
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }

            return result;
        }
    }
}
