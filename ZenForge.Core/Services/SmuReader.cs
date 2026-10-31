using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using ZenStates.Core;
using ZenForge.Helpers;

namespace ZenForge.Services
{
    /// <summary>
    /// SMU 服务：只读探测 + 受控写入。
    ///
    /// 设计原则：
    ///   1. 写入边界由调用方校验（CO ∈ [-50, +50] 含加压，FMax ∈ [400, 5400]）；
    ///      本类对传入值只做输入合法性检查，不做“安全区间”限制——用户对硬件负全责。
    ///   2. 每次写入后立即回读校验，回读值与写入值不一致视为失败，上报日志 / 弹窗。
    ///   3. SMU 未初始化（驱动缺失 / 未提权）时，所有读写一律短路返回，绝不抛异常。
    ///   4. 仅暴露经过验证的高层 Set（SetPsmMarginSingleCore / SetFMax），
    ///      不提供任何写寄存器 / 原始 mailbox 的底层接口。
    /// </summary>
    public sealed class SmuReader : IDisposable
    {
        private Cpu? _cpu;

        // 由 ReadSnapshot 填充，供写入时编码核心掩码（避免重复探测拓扑）
        private uint _coresPerCcd;
        private int _coreCount;

        /// <summary>
        /// SMU / PCI 总线访问锁。
        ///
        /// 为什么必须有：
        ///   页面探测（ReadSnapshot）与 CO/FMax 写入共用同一个 Cpu 实例，
        ///   而 ZenStates-Core 内部的 PCI 总线互斥只在单个方法调用期间生效。
        ///   并发进入会互相踩踏总线，导致回读校验读到写了一半的状态。
        ///
        /// 【为什么是 static】
        ///   进程内可能存在不止一个 SmuReader：Shared 单例，以及 WaitForPawnIO 在
        ///   开机静默自启时临时创建、用完即弃的实例（它刻意绕开 Shared 的失败缓存）。
        ///   锁若是实例字段，两者各持一把，等于没有互斥——启动后 60 秒内打开调校页，
        ///   页面探测可能与后台自启写入同时碰同一个 SMU 邮箱，回读就会莫名不一致。
        ///   改成静态后，无论多少个实例，进程内只有一把锁。
        /// </summary>
        private static readonly object _syncRoot = new();

        /// <summary>ZenStates-Core 是否初始化成功（PawnIO 驱动 + 管理员权限就绪）。</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>初始化或读取失败的原因。</summary>
        public string? LastError { get; private set; }

        /// <summary>探测到的物理核心数（未探测时为 0）。</summary>
        public int CoreCount => _coreCount;

        /// <summary>每 CCD 的核心数（未探测时回退为 8，安全默认值）。</summary>
        public uint CoresPerCcd => _coresPerCcd == 0 ? 8u : _coresPerCcd;

        /// <summary>
        /// 每 CCD 核心数的 **原始探测值**：0 表示还没探测到。
        /// 需要区分「真的读到 8 核/CCD」和「没读到所以兜底 8」时必须用这个，
        /// 否则任何拓扑推断都会把兜底值当成事实。
        /// </summary>
        public uint CoresPerCcdRaw => _coresPerCcd;

        // ---------- 单例：进程内只创建一个 Cpu，且永不销毁 ----------
        //
        // 这是硬约束，原因在库的实现里：
        //   Cpu.Dispose() -> Opcode.Close() + Mutexes.Close() + _pawnRyzenSmu.Dispose()
        // 而 Opcode 是 internal static class，codeBuffer / Cpuid / Rdtsc 都是**静态**的，
        // 即进程级共享。所以只要销毁任何一个 Cpu 实例：
        //   · Opcode.Close() 会释放那块静态代码缓存；
        //   · 之后**所有** Cpu（包括没被销毁的那个）都不再能执行 CPUID / SMU 命令。
        // 表现出来就是「SMU 可用，但读 CO 全部 null、读 FMax 全部 0」，
        // 因为 CPUID 缓存下来的 CPU 名 / 代号 / SMU 版本仍然正常显示。
        //
        // 曾经踩过的两个坑（0.7.1 / 0.7.2 的实测报告）都属于这一类：
        //   · WaitForPawnIO 另建一个 Cpu，自动调校结束后 Dispose 掉它 —— 顺手把共享实例打哑；
        //   · WaitForPawnIO 改成复用单例后，调用方的 finally reader.Dispose() 直接把单例打死。
        //
        // 因此现在的规则是：
        //   1. 进程内最多创建一个 Cpu；
        //   2. 任何路径都不 Dispose（Dispose 本身已改为 no-op）；
        //   3. 驱动没就绪时只重试「测试消息」，不重建实例。
        private static SmuReader? _shared;
        private static readonly object _sharedLock = new();

        /// <summary>
        /// 本进程累计创建过多少个 Cpu 实例。正常必须是 1。
        /// 大于 1 虽不再致命（已禁止 Dispose），但仍说明有重复创建路径，自检会报出来。
        /// </summary>
        private static int _createdCount;
        internal static int CreatedCount => Volatile.Read(ref _createdCount);

        /// <summary>上一次探测失败的时间戳（Environment.TickCount64）。</summary>
        private static long _lastFailureTick = long.MinValue / 2;

        /// <summary>失败后的重试冷却（毫秒）。避免每次访问都发一遍 SMU 测试消息。</summary>
        private const int RetryCooldownMs = 2000;

        /// <summary>
        /// 进程内共享的 SMU 实例。
        /// 首次访问创建；此后无论可用与否都返回同一个对象，只重新探测可用性。
        /// </summary>
        public static SmuReader Shared
        {
            get
            {
                lock (_sharedLock)
                {
                    if (_shared == null)
                    {
                        _shared = TryCreate(out _);
                        return _shared;
                    }

                    if (_shared.IsAvailable)
                        return _shared;

                    // 已创建但当前不可用：按冷却重试探测，绝不重建
                    long now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref _lastFailureTick) < RetryCooldownMs)
                        return _shared;

                    Interlocked.Exchange(ref _lastFailureTick, now);
                    _shared.RetryAvailabilityProbe();
                    return _shared;
                }
            }
        }

        /// <summary>
        /// 只读取「是否已有可用实例」，不会触发创建。
        /// 供拓扑推断这类「拿不到也能自己兜底」的场景使用 ——
        /// 早期调用 Shared 会逼着在 PawnIO 还没就绪时建实例。
        /// </summary>
        public static uint PeekCoresPerCcdRaw()
        {
            lock (_sharedLock)
            {
                return _shared is { IsAvailable: true } r ? r._coresPerCcd : 0u;
            }
        }

        /// <summary>
        /// 重新探测可用性。若之前连 Cpu 都没建起来（驱动未就绪），这次补建；
        /// 若已经有 Cpu 对象，只补发一次测试消息。
        /// </summary>
        private void RetryAvailabilityProbe()
        {
            if (_cpu == null)
            {
                try
                {
                    _cpu = new Cpu();
                    Interlocked.Increment(ref _createdCount);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return;
                }
            }

            try
            {
                if (_cpu.SendTestMessage())
                {
                    IsAvailable = true;
                    LastError = null;
                    return;
                }

                LastError = "SMU 未应答测试消息";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private SmuReader()
        {
        }

        /// <summary>
        /// 等待 PawnIO 驱动就绪（自启 / 静默场景专用）。
        ///
        /// 为什么不用 <see cref="Shared"/>：Shared 是失败也缓存的懒加载单例——
        /// 若启动时驱动还没加载好，单例会永久缓存一个不可用的实例，
        /// 导致「开机静默自启」永远写不进 SMU。这里每轮都重建 reader（绕开缓存），
        /// 一旦探测到 IsAvailable 即返回该可用实例；超过 maxSeconds 仍不可用则返回 null。
        /// </summary>
        /// <param name="maxSeconds">最长等待秒数（默认 60）。</param>
        /// <param name="token">取消令牌：关机或用户放弃时可中断等待，不拖住后台线程。</param>
        public static SmuReader? WaitForPawnIO(int maxSeconds = 60, CancellationToken token = default)
        {
            // 已经有可用实例就直接用（Shared 保证进程内只有一个 Cpu）。
            if (Shared is { IsAvailable: true } existing)
                return existing;

            // best-effort 先尝试唤醒候选驱动服务，给加载器一点时间
            TryWakeDriverServices();

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSeconds)
            {
                if (token.IsCancellationRequested) return null;

                // 清掉冷却时间戳，让 Shared 立刻重发一次 SMU 测试消息。
                // 注意：这里只是「重新探测」，不会重建 Cpu、也不会 Dispose 任何东西。
                Interlocked.Exchange(ref _lastFailureTick, long.MinValue / 2);
                SmuReader reader = Shared;
                if (reader.IsAvailable)
                    return reader;

                // 用可取消的等待代替 Thread.Sleep：驱动常在几十毫秒内就绪，
                // 固定睡满 1 秒会让首次写入白白延后。250ms 轮询兼顾响应与开销。
                if (token.WaitHandle.WaitOne(250))
                    return null;
            }

            LogService.Error($"PawnIO 未加载（{maxSeconds}s 内未就绪）");
            return null;
        }

        /// <summary>
        /// best-effort 唤醒候选内核驱动服务（PawnIO / PawnIO2 等）。
        /// 服务不存在、未安装或权限不足时静默忽略，绝不崩溃。
        /// </summary>
        private static void TryWakeDriverServices()
        {
            string[] candidates = { "PawnIO", "PawnIO2", "pawnio" };
            foreach (string name in candidates)
            {
                try
                {
                    using ServiceController sc = new(name);
                    if (sc.Status != ServiceControllerStatus.Running)
                        sc.Start();
                }
                catch
                {
                    // 服务不存在 / 无权限 / 不支持：忽略
                }
            }
        }

        /// <summary>
        /// 尝试创建 SMU 读取器。PawnIO 驱动缺失或未提权时不会抛出，
        /// 而是返回 IsAvailable=false 的实例（错误信息在 initError 中）。
        /// </summary>
        public static SmuReader TryCreate(out string? initError)
        {
            SmuReader reader = new();
            try
            {
                reader._cpu = new Cpu();
                Interlocked.Increment(ref _createdCount);

                // 「构造成功」不等于「SMU 能应答」。
                //
                // Cpu 的构造函数在 SendTestMessage() 失败时只记 LastError、不抛异常，
                // 甚至把 Status 设成 OK。旧版只看有没有抛异常，于是会把一个
                // SMU 已不应答的实例当成可用并缓存起来 —— 之后读 CO 全 null、
                // 读 FMax 全 0，界面却一直显示「SMU 可用」。
                // 这里补一次真实校验：直接发一次 SMU 测试消息。
                // 不用 LastError 判断 —— 它也被「拓扑读取失败」这类非致命问题写入，
                // 拿它一刀切会把本来能用的平台误判掉。
                bool responding;
                try
                {
                    responding = reader._cpu.SendTestMessage();
                }
                catch (Exception ex)
                {
                    responding = false;
                    reader.LastError = ex.Message;
                }

                if (!responding)
                {
                    // 不能 Dispose：Cpu.Dispose 会 Opcode.Close()，而那是进程级静态资源，
                    // 一关就把「所有」Cpu 的 CPUID / SMU 能力一起废掉。
                    // 这里保留实例，稍后只重发一次测试消息（见 RetryAvailabilityProbe）。
                    reader.IsAvailable = false;
                    reader.LastError ??= "SMU 未应答测试消息（驱动可能尚未就绪）";
                    initError = reader.LastError;
                    LogService.Warn($"ZenStates-Core 已构造但 SMU 无应答：{reader.LastError}");
                    return reader;
                }

                reader.IsAvailable = true;
                initError = null;
                LogService.Info($"ZenStates-Core 初始化成功：{reader._cpu.info.cpuName} ({reader._cpu.info.codeName})");
            }
            catch (Exception ex)
            {
                reader.IsAvailable = false;
                reader.LastError = ex.Message;
                initError = ex.Message;
                LogService.Error($"ZenStates-Core 初始化失败: {ex}");
            }

            return reader;
        }

        /// <summary>执行一次完整的只读探测。</summary>
        public SmuSnapshot ReadSnapshot()
        {
            SmuSnapshot snapshot = new();
            if (_cpu == null || !IsAvailable)
            {
                snapshot.Success = false;
                snapshot.Error = LastError ?? "SMU 未初始化";
                return snapshot;
            }

            // 与遥测刷新 / 写入串行化，避免争抢 PCI 总线
            lock (_syncRoot)
            {
                return ReadSnapshotCore(snapshot);
            }
        }

        private SmuSnapshot ReadSnapshotCore(SmuSnapshot snapshot)
        {
            try
            {
                Cpu cpu = _cpu!;

                // ---------- 1. CPU 基本信息 ----------
                snapshot.CpuName = cpu.info.cpuName ?? string.Empty;
                snapshot.CodeName = cpu.info.codeName.ToString();
                snapshot.Family = cpu.info.family.ToString();
                snapshot.SmuType = cpu.smu.SMU_TYPE.ToString();
                snapshot.Ccds = cpu.info.topology.ccds;
                snapshot.Cores = cpu.info.topology.cores;
                snapshot.PhysicalCores = cpu.info.topology.physicalCores;
                snapshot.ThreadsPerCore = cpu.info.topology.threadsPerCore;

                snapshot.SmuVersion = SmuVersionString(cpu);

                LogService.Info(
                    $"CPU: {snapshot.CpuName} | {snapshot.CodeName} | {snapshot.Family} | " +
                    $"SMU {snapshot.SmuVersion} ({snapshot.SmuType}) | " +
                    $"{snapshot.Ccds} CCD / {snapshot.Cores} 核 / {snapshot.ThreadsPerCore} 线程每核");

                // ---------- 2. 每核 Curve Optimizer 当前值 ----------
                uint coresPerCcd = cpu.info.topology.coresPerCcx;
                if (coresPerCcd == 0)
                    coresPerCcd = 8; // GraniteRidge（9955HX / Fire Range）为 1 CCX × 8 核 per CCD

                int coreCount = (int)cpu.info.topology.physicalCores;
                snapshot.CoreMargins = new int[coreCount];
                int okCount = 0;
                for (int i = 0; i < coreCount; i++)
                {
                    (uint core, uint ccd, uint ccx) = SplitCore(i);
                    uint? margin = cpu.GetPsmMarginSingleCore(core, ccd, ccx);
                    if (margin.HasValue)
                    {
                        // SMU 返回无符号计数，取负视角即为 CO 值（负数 = 降压）
                        snapshot.CoreMargins[i] = unchecked((int)margin.Value);
                        okCount++;
                    }
                    else
                    {
                        snapshot.CoreMargins[i] = int.MinValue; // 读取失败或被熔断禁用
                    }
                }

                _coresPerCcd = coresPerCcd;
                _coreCount = coreCount;
                snapshot.CoresPerCcd = coresPerCcd;
                LogService.Info($"Curve Optimizer 读取完成：{okCount}/{coreCount} 个核心返回有效值");
                LogService.Info($"核心拓扑：每 CCD {coresPerCcd} 核，共 {coreCount} 物理核心");

                // ---------- 3. FMax 当前限制（MHz） ----------
                snapshot.FMaxMHz = cpu.GetFMax();
                LogService.Info($"FMax 当前上限：{snapshot.FMaxMHz} MHz");

                snapshot.Success = true;
            }
            catch (Exception ex)
            {
                snapshot.Success = false;
                snapshot.Error = ex.Message;
                LogService.Error($"SMU 只读探测失败: {ex}");
            }

            return snapshot;
        }

        // 受控写入

        /// <summary>
        /// CO 允许范围：[-50, +50]。
        /// 负值 = 降压（undervolt），正值 = 加压（overvolt）。
        /// 该范围仅为“输入合法性”边界，不再作为安全拦截：用户对其硬件负全责。
        /// </summary>
        public const int CoMin = -50;
        public const int CoMax = 50;

        /// <summary>FMax 允许范围（MHz）。硬件步进约 25 MHz，范围取 [4450, 5650]。</summary>
        public const uint FMaxMin = 4450;
        public const uint FMaxMax = 5650;

        /// <summary>
        /// FMax 回读容差（MHz）。SMU 按硬件步进量化写入值，回读与请求未必严格相等，
        /// 故允许 ≤10 MHz 的偏差（严格按硬件步进放宽，防止实际没写进去却误判成功）。
        /// </summary>
        public const int FMaxReadTolerance = 10;

        /// <summary>
        /// 写入每核 Curve Optimizer（负压偏移）。
        /// 调用方应已通过 <see cref="TuningStore"/> 的范围钳制，这里对越界值再做二次拒绝。
        /// 每个核心写入后立即回读校验。
        /// </summary>
        public SmuWriteResult WriteCurveOptimizer(IReadOnlyList<int> margins)
        {
            SmuWriteResult result = new();
            if (_cpu == null || !IsAvailable)
            {
                result.Fatal = LastError ?? "SMU 未初始化";
                return result;
            }

            // 写入期间独占总线：绝不能让 2 秒遥测定时器插进来读寄存器，
            // 否则可能读到写了一半的状态，也与回读校验互相干扰。
            lock (_syncRoot)
            {
                return WriteCurveOptimizerCore(margins, result);
            }
        }

        private SmuWriteResult WriteCurveOptimizerCore(IReadOnlyList<int> margins, SmuWriteResult result)
        {
            Cpu cpu = _cpu!;
            int count = _coreCount > 0 ? Math.Min(margins.Count, _coreCount) : margins.Count;

            // 写入前先把拓扑与 SMU 型号打出来：CO 被整片拒绝时，最常见的原因就是
            // 核心序号 → (core, ccd, ccx) 的拆分与 SMU 的预期不一致，
            // 而这条信息只有在这里才看得到。
            LogService.Info(
                $"CO 写入开始：{count} 核，每 CCD {CoresPerCcd} 核，" +
                $"SMU 型号 {cpu.smu.SMU_TYPE}，版本 0x{cpu.smu.Version:X}");
            for (int i = 0; i < count; i++)
            {
                int value = margins[i];

                // int.MinValue 表示“跳过该核心”（熔断 / 读取失败 / 不存在），不下发
                if (value == int.MinValue)
                    continue;

                // 输入合法性边界：[-50, +50]（含正值加压）。仅拦截明显的输入错误，
                // 不再限制在固定的“安全降压区间”，用户对自己的硬件负全责。
                if (value < CoMin || value > CoMax)
                {
                    result.WriteFailed.Add(i);
                    result.RejectedReasons[i] = $"越界({value})";
                    LogService.Error($"核心 {i} CO 值 {value} 越界，已拒绝下发");
                    continue;
                }

                (uint core, uint ccd, uint ccx) = SplitCore(i);
                bool accepted = false;
                uint coreMask = 0;
                try
                {
                    coreMask = cpu.MakeCoreMask(core, ccd, ccx);
                    accepted = cpu.SetPsmMarginSingleCore(core, ccd, ccx, value);
                }
                catch (Exception ex)
                {
                    LogService.Error($"SetPsmMarginSingleCore 核心 {i} 异常: {ex}");
                }

                LogService.Info(
                    $"  核 {i,2} → core={core} ccd={ccd} ccx={ccx} mask=0x{coreMask:X8} " +
                    $"值={value} 接受={accepted}");

                if (!accepted)
                {
                    result.WriteFailed.Add(i);
                    LogService.Error($"核心 {i} CO 写入被 SMU 拒绝（值 {value}）");
                    continue;
                }

                // 回读校验：SMU 返回无符号计数，按 2 补码解释为负值视角
                int readBack = int.MinValue;
                try
                {
                    uint? rb = cpu.GetPsmMarginSingleCore(core, ccd, ccx);
                    if (rb.HasValue)
                        readBack = unchecked((int)rb.Value);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"GetPsmMarginSingleCore 回读 核心 {i} 异常: {ex.Message}");
                }

                if (readBack != value)
                {
                    result.VerifyFailed.Add(i);
                    result.VerifiedValue[i] = readBack;
                    LogService.Error($"核心 {i} CO 回读不一致：写入 {value}，回读 {readBack}");
                }
                else
                {
                    result.Applied[i] = value;
                }
            }

            result.AllSucceeded = result.WriteFailed.Count == 0 && result.VerifyFailed.Count == 0;
            return result;
        }

        /// <summary>
        /// 写入 FMax 频率上限（MHz）。写入后立即回读校验。
        /// </summary>
        public SmuFMaxResult WriteFMax(uint mhz)
        {
            SmuFMaxResult result = new() { Requested = mhz };
            if (_cpu == null || !IsAvailable)
            {
                result.Fatal = LastError ?? "SMU 未初始化";
                return result;
            }

            // 二次安全断言：越界值绝不下发
            if (mhz < FMaxMin || mhz > FMaxMax)
            {
                result.Fatal = $"FMax 越界 {mhz}（允许 {FMaxMin}-{FMaxMax}）";
                LogService.Error(result.Fatal);
                return result;
            }

            try
            {
                lock (_syncRoot)
                {
                    // 写入前先读一次基线：如果基线本身就是 0，说明这台机器的
                    // FMax 读取通道不可用（部分移动平台 SMU 不实现该消息），
                    // 那么「写入后回读 0」不能当作写入失败 —— 要能区分这两件事。
                    uint before = _cpu.GetFMax();
                    LogService.Info($"FMax 写入前基线：{before} MHz");

                    result.SmuAccepted = _cpu.SetFMax(mhz);
                    result.ReadBack = _cpu.GetFMax();

                    LogService.Info(
                        $"FMax 写入：请求 {mhz}，SMU 接受 {result.SmuAccepted}，" +
                        $"回读 {result.ReadBack}（基线 {before}）");
                }

                // 回读容差：SMU 按硬件步进量化，允许 ≤ FMaxReadTolerance MHz 的偏差；
                // 偏差过大视为实际未写进去，仍判失败（防止误判成功）。
                result.AllSucceeded = result.SmuAccepted &&
                    Math.Abs((long)result.ReadBack - (long)mhz) <= FMaxReadTolerance;
                if (!result.AllSucceeded)
                    LogService.Error($"FMax 回读不一致：请求 {mhz}，SMU 接受 {result.SmuAccepted}，回读 {result.ReadBack}");
            }
            catch (Exception ex)
            {
                result.Fatal = ex.Message;
                LogService.Error($"SetFMax 异常: {ex}");
            }

            return result;
        }

        /// <summary>
        /// 把「线性核序号」拆成库要求的 (core, ccd, ccx) 三元组。
        ///
        /// 掩码编码必须交给库的 MakeCoreMask —— 它按 SMU 型号区分 APU / 桌面 /
        /// EPYC 的分支，自己按「标准公式」手搓会漏掉这些分支，导致 SMU 直接拒收。
        /// （0.6.x 就是这么写的：读能读到值，写全被拒，16 核无一例外。）
        ///
        /// ccx 取 0：Zen 3 之后消费级平台是 CCD 内含单个 CCX，
        /// corners 全部落在 ccd 维度；EPYC 那种一 CCD 多 CCX 的形态不在本工具范围。
        /// </summary>
        private (uint Core, uint Ccd, uint Ccx) SplitCore(int coreIndex)
        {
            int perCcd = CoresPerCcd > 0 ? (int)CoresPerCcd : 8;
            uint ccd = (uint)(coreIndex / perCcd);
            uint local = (uint)(coreIndex % perCcd);
            return (local, ccd, 0);
        }

        // ==================== 诊断专用入口（仅 SmuDiagnostics 使用） ====================
        // 这些方法只是把内部能力暴露给自检报告，不参与业务逻辑。
        // 之所以要单独开：自检需要在「不改变任何状态」的前提下逐项验证，
        // 而复用 WriteCurveOptimizer 会带上范围校验与回读判定，不适合做探针。

        /// <summary>把线性核序号拆成 (core, ccd, ccx)，供报告展示。</summary>
        internal (uint Core, uint Ccd, uint Ccx) SplitCoreForDiagnostics(int coreIndex)
            => SplitCore(coreIndex);

        /// <summary>调用库的 MakeCoreMask，供报告展示实际掩码值。</summary>
        internal uint MakeCoreMaskForDiagnostics(uint core, uint ccd, uint ccx)
        {
            if (_cpu == null)
                return 0;
            return _cpu.MakeCoreMask(core, ccd, ccx);
        }

        /// <summary>直接下发一次 CO（不做范围校验、不回读），返回 SMU 是否接受。</summary>
        internal bool TrySetCoForDiagnostics(uint core, uint ccd, uint ccx, int margin)
        {
            if (_cpu == null || !IsAvailable)
                return false;

            lock (_syncRoot)
            {
                try
                {
                    return _cpu.SetPsmMarginSingleCore(core, ccd, ccx, margin);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>底层 Cpu 的状态与错误，供自检报告判断「SMU 是否真的在应答」。</summary>
        internal (string Status, string? LastError) CpuStateForDiagnostics()
        {
            Cpu? cpu = _cpu;
            if (cpu == null)
                return ("(无 Cpu 实例)", LastError);

            return (cpu.Status.ToString(), cpu.LastError?.Message);
        }

        /// <summary>单独跑一次 SMU 测试消息，看邮箱是否真的通。</summary>
        internal (bool Ok, string? Error) TestMessageForDiagnostics()
        {
            Cpu? cpu = _cpu;
            if (cpu == null || !IsAvailable)
                return (false, LastError ?? "SMU 未初始化");

            lock (_syncRoot)
            {
                try
                {
                    return (cpu.SendTestMessage(), null);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            }
        }

        /// <summary>
        /// 当前平台实际可用的 SMU 消息号。
        /// 为 0 表示该平台没有初始化这条命令 —— 那才是「命令不被支持」的硬证据，
        /// 比「返回 bool false」能说明更多问题。
        /// </summary>
        internal (uint GetDldo, uint SetDldo, uint SetBoostAll, uint GetBoost) SmuMsgIdsForDiagnostics()
        {
            Cpu? cpu = _cpu;
            if (cpu == null)
                return (0, 0, 0, 0);

            return (cpu.smu.Rsmu.SMU_MSG_GetDldoPsmMargin,
                    cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin,
                    cpu.smu.Rsmu.SMU_MSG_SetBoostLimitFrequencyAllCores,
                    cpu.smu.Rsmu.SMU_MSG_GetBoostLimitFrequency);
        }

        /// <summary>直接读一次 FMax，不缓存、不做容差判断。</summary>
        internal uint GetFMaxForDiagnostics()
        {
            if (_cpu == null || !IsAvailable)
                return 0;

            lock (_syncRoot)
            {
                try
                {
                    return _cpu.GetFMax();
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>SMU 版本的统一字符串形式，与 ReadSnapshot 的写法保持一致。</summary>
        private static string SmuVersionString(Cpu cpu)
        {
            uint version = cpu.smu.Version;
            if (version == 0)
                version = cpu.GetSmuVersion();
            return version == 0 ? "未知" : $"0x{version:X}";
        }

        /// <summary>
        /// 故意留空。
        ///
        /// Cpu.Dispose() 会依次调用 Opcode.Close() / Mutexes.Close() / RyzenSmu.Dispose，
        /// 其中 Opcode 是 internal static class，内部缓存（codeBuffer、Cpuid、Rdtsc）
        /// 都是进程级静态资源。只要有一个 Cpu 被销毁，**所有** Cpu 的
        /// CPUID 与 SMU 命令都会失效 —— 而失败现场看起来只是「读不到 CO / FMax」，
        /// 极难反推。
        ///
        /// 本工具是单实例桌面程序，Cpu 的生命周期本就等于进程生命周期，
        /// 因此这里不做任何释放：既避免误伤，也不存在真正的泄漏问题。
        /// 保留 IDisposable 实现只是为了不给调用方的 using 写法添麻烦。
        /// </summary>
        public void Dispose()
        {
            // 见上：刻意不释放任何东西
        }
    }

    /// <summary>一次只读探测的结果快照。</summary>
    public sealed class SmuSnapshot
    {
        public bool Success { get; set; }
        public string? Error { get; set; }

        public string CpuName { get; set; } = string.Empty;
        public string CodeName { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string SmuType { get; set; } = string.Empty;
        public string SmuVersion { get; set; } = string.Empty;
        public uint Ccds { get; set; }
        public uint Cores { get; set; }
        public uint PhysicalCores { get; set; }
        public uint ThreadsPerCore { get; set; }
        public uint CoresPerCcd { get; set; }

        /// <summary>每核 CO 值（int.MinValue = 读取失败/禁用）。</summary>
        public int[] CoreMargins { get; set; } = Array.Empty<int>();

        /// <summary>当前 FMax 上限（MHz，0 = 未知）。</summary>
        public uint FMaxMHz { get; set; }
    }

    /// <summary>Curve Optimizer 写入结果（含逐核回读校验）。</summary>
    public sealed class SmuWriteResult
    {
        /// <summary>全部核心均写入成功且回读一致。</summary>
        public bool AllSucceeded { get; set; }

        /// <summary>致命错误（如 SMU 未初始化），此时不会有任何核心被写入。</summary>
        public string? Fatal { get; set; }

        /// <summary>SMU 拒绝写入（SetPsmMarginSingleCore 返回 false）或越界被拦截的核心索引。</summary>
        public List<int> WriteFailed { get; } = new();

        /// <summary>写入成功但回读值与目标不一致的核心索引。</summary>
        public List<int> VerifyFailed { get; } = new();

        /// <summary>成功写入并回读一致的核心 → 值。</summary>
        public Dictionary<int, int> Applied { get; } = new();

        /// <summary>回读不一致的核心 → 实际回读值。</summary>
        public Dictionary<int, int> VerifiedValue { get; } = new();

        /// <summary>被拦截（越界等）的核心 → 原因。</summary>
        public Dictionary<int, string> RejectedReasons { get; } = new();
    }

    /// <summary>FMax 写入结果（含回读校验）。</summary>
    public sealed class SmuFMaxResult
    {
        public bool AllSucceeded { get; set; }
        public bool SmuAccepted { get; set; }
        public uint Requested { get; set; }
        public uint ReadBack { get; set; }
        public string? Fatal { get; set; }
    }
}
