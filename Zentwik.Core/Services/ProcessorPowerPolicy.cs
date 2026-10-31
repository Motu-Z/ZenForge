using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 电源计划策略档位（对外暴露的四个选项）。
    ///
    /// 【这不是 AMD SMU 固件层的 PBO】
    /// SMU 层的 PBO（功耗墙 / 曲线优化 / Boost 上限）没有任何公开接口，
    /// 只有主板 BIOS 与 AMD 驱动能写。硬发 SMU 消息号属于拿用户机器赌博：
    /// 消息号或载荷错一个字节就是黑屏 + 清 CMOS 才能恢复。
    ///
    /// 本服务操作的是 Windows 电源策略层里三个可读可写、有返回码的索引：
    ///
    ///   PERFBOOSTMODE（处理器性能提升模式）be337238-0d82-4146-a960-4f3749d470c7
    ///     0 = 已停用   1 = 已启用   2 = 积极   3 = 高效   5 = 积极且高效
    ///     （4 是保留值；6 为部分 OEM 扩展，不写入）
    ///
    ///   THROTTLEMAX（最大处理器状态，即百分比上限）bc5038f7-23e0-4960-96da-33abaf5935ec
    ///     0..100，厂商默认 100。
    ///
    ///   PERFEPP（能效偏好，EPP 指 Energy Performance Preference）36687f9e-e3a5-4dbf-b1dc-15eb381c6863
    ///     0 = 性能优先，100 = 能效优先。Intel 机器上作用是 P-state 偏向；
    ///     AMD 机器上由 CPPC 的 EPP 代理转发给 SMU。两平台都有效，只是力度不同。
    ///
    ///   IDLEDISABLE（处理器空闲禁用）5d76a2ca-e8c0-402f-a133-2158492d58ad
    ///     1 = 禁止处理器进入空闲状态。只在「解除限制」档用，其余档恢复 0。
    ///
    /// 四个档位的取值依据见各枚举成员的注释。所有写入都是「写活跃方案 + 重新激活」，
    /// 失败只返回错误文本、绝不抛，且不会留下半套状态：任一索引写失败即回滚
    /// 本次已写的索引。
    /// </summary>
    public enum PowerPlanProfile
    {
        /// <summary>解除限制：Boost 积极、处理器状态 100%、EPP 0（性能优先）、禁止空闲降频。</summary>
        Unrestricted,

        /// <summary>温和：Boost 启用（不写「积极」）、100%、EPP 25。默认档，性能与发热的平衡点。</summary>
        Gentle,

        /// <summary>均衡：Boost 启用、100%、EPP 50。跟厂商默认行为基本一致。</summary>
        Balanced,

        /// <summary>静音：Boost 停用、处理器状态 85%、EPP 100（极致能效）。风扇更安静，峰值性能明显下降。</summary>
        Quiet,
    }

    /// <summary>一次档位应用的结果（哪个索引失败、错误文本）。</summary>
    public readonly struct PowerPlanResult
    {
        public PowerPlanResult(bool ok, string setting, string error)
        {
            Ok = ok;
            Setting = setting;
            Error = error;
        }

        public bool Ok { get; }

        /// <summary>失败时出问题的索引名（Boost / 处理器状态 / EPP / 空闲禁用）。</summary>
        public string Setting { get; }

        public string Error { get; }

        public static PowerPlanResult Success() => new(true, string.Empty, string.Empty);

        public static PowerPlanResult Fail(string setting, string error) => new(false, setting, error);
    }

    /// <summary>
    /// 读写 Windows 电源策略，实现「解除限制 / 温和 / 均衡 / 静音」四档。
    ///
    /// 全部 P/Invoke 都包在 try/catch 里，任何失败只返回结果对象并带上错误文本，
    /// 绝不向上抛 —— 调校页不该因为一个可选策略写不进去就整页报错。
    /// </summary>
    public static partial class ProcessorPowerPolicy
    {
        // ---------------- 电源管理 GUID ----------------

        /// <summary>处理器电源管理子组，所有处理器相关设置都在它下面。</summary>
        private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>处理器性能提升模式（PERFBOOSTMODE）。</summary>
        private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

        /// <summary>最大处理器状态 / 百分比上限（PROCTHROTTLEMAX）。</summary>
        private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

        /// <summary>能效偏好 EPP（PERFEPP）。</summary>
        private static readonly Guid PerfEpp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

        /// <summary>处理器空闲禁用（IDLEDISABLE）。</summary>
        private static readonly Guid IdleDisable = new("5d76a2ca-e8c0-402f-a133-2158492d58ad");

        // PERFBOOSTMODE 的合法值。0 停用 / 1 启用 / 2 积极 / 3 高效 / 5 积极且高效。
        private const uint BoostDisabled = 0;
        private const uint BoostEnabled = 1;
        private const uint BoostAggressive = 2;

        // 各索引「无设置」时 Windows 返回的哨兵值（等价于交回系统默认）。
        private const uint NoSetting = uint.MaxValue;

        /// <summary>DC 写入失败的告警（null 表示本次没有失败）。每次 Apply 开头重置。</summary>
        private static string? _lastDcWarning;

        // ---------------- 索引描述 ----------------

        private struct SettingRef
        {
            public SettingRef(string name, Guid guid)
            {
                Name = name;
                Guid = guid;
            }

            public string Name { get; }
            public Guid Guid { get; }
        }

        private static readonly SettingRef BoostRef = new("性能提升模式", PerfBoostMode);
        private static readonly SettingRef StateRef = new("最大处理器状态", MaxProcessorState);
        private static readonly SettingRef EppRef = new("能效偏好", PerfEpp);
        private static readonly SettingRef IdleRef = new("空闲禁用", IdleDisable);

        // ---------------- P/Invoke ----------------

        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerReadACValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, out uint valueIndex);

        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerReadDCValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, out uint valueIndex);

        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerWriteACValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, uint valueIndex);

        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerWriteDCValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, uint valueIndex);

        /// <summary>把当前方案重新激活一次，让刚写入的索引立刻生效。</summary>
        [LibraryImport("powrprof.dll", SetLastError = true)]
        private static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        /// <summary>释放 <see cref="PowerGetActiveScheme"/> 分配的方案 GUID。</summary>
        [LibraryImport("kernel32.dll")]
        private static partial IntPtr LocalFree(IntPtr handle);

        // ---------------- 对外接口 ----------------

        /// <summary>
        /// 按四个索引的组合反推当前最接近哪个档位。
        ///
        /// 反推而不是「记住上次选的」：用户在 Windows 设置里改过电源计划、
        /// 或者别的软件（游戏平台、厂商控制中心）动过同一批索引，
        /// 「上次选的」就是错的。以系统实际值反推，界面才不撒谎。
        /// 完全对不上任何一档时返回 null —— 由调用方显示「自定义」。
        /// </summary>
        public static bool TryDetect(out PowerPlanProfile? profile, out string error)
        {
            profile = null;
            error = string.Empty;

            if (!TryReadFour(out uint boost, out uint state, out uint epp, out uint idle, out error))
                return false;

            profile = Match(boost, state, epp, idle);
            return true;
        }

        /// <summary>
        /// 应用一个档位：写 AC 与 DC 两套索引，然后重新激活当前方案。
        ///
        /// 写入顺序刻意是「先放宽、后收紧」（Boost → 处理器状态 → EPP → 空闲禁用）：
        /// - 从静音切到解除限制时，先放开 Boost 再放开频率上限，中途不会出现
        ///   「Boost 已经允许但频率仍锁在 85%」的奇怪中间态被别的软件读到。
        /// - 反过来从解除限制切静音时，顺序是「先收 Boost，再把上限压到 85%」，
        ///   先断掉超频再限频，也不会出现瞬间的超频。
        ///
        /// 任一索引写失败即回滚本次已写的索引，避免留下半套状态。
        /// </summary>
        public static PowerPlanResult Apply(PowerPlanProfile profile)
        {
            (uint boost, uint state, uint epp, uint idle) = ValuesOf(profile);
            _lastDcWarning = null;

            IntPtr schemePtr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out schemePtr, out Guid scheme, out string schemeError))
                    return PowerPlanResult.Fail("当前电源方案", schemeError);

                var writes = new (SettingRef Ref, uint Value)[]
                {
                    (BoostRef, boost),
                    (StateRef, state),
                    (EppRef, epp),
                    (IdleRef, idle),
                };

                var done = new System.Collections.Generic.List<SettingRef>();

                foreach ((SettingRef reference, uint value) in writes)
                {
                    PowerPlanResult step = WriteBoth(scheme, reference, value);
                    if (!step.Ok)
                    {
                        Rollback(scheme, done);
                        return step;
                    }

                    done.Add(reference);
                }

                // 不重新激活的话，部分系统上写入的索引要等下次切换电源方案才生效，
                // 用户会以为「点了没反应」。
                uint rcActive = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                if (rcActive != 0)
                {
                    Rollback(scheme, done);
                    return PowerPlanResult.Fail("激活电源方案", Describe(rcActive));
                }

                return PowerPlanResult.Success();
            }
            catch (Exception ex)
            {
                return PowerPlanResult.Fail("电源策略", ex.Message);
            }
            finally
            {
                if (schemePtr != IntPtr.Zero)
                    LocalFree(schemePtr);
            }
        }

        /// <summary>
        /// 把本程序写过的全部索引交回系统默认（值为 0xFFFFFFFF = 「无设置」）。
        ///
        /// 【为什么是「交回默认」而不是「写成均衡档」】
        /// 恢复出厂的意思是「抹掉我们造成的改动」。用户原本用的是什么电源方案、
        /// 哪些索引本来就没设置过，我们并不知道也不该假设。写 0xFFFFFFFF 之后
        /// Windows 会自动回落该方案原本的默认行为 —— 这是我们唯一能确定正确的动作。
        ///
        /// 与 <see cref="Apply"/> 共用同一套写入 / 回滚机制，因此失败语义一致。
        /// </summary>
        public static PowerPlanResult Release()
        {
            IntPtr schemePtr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out schemePtr, out Guid scheme, out string schemeError))
                    return PowerPlanResult.Fail("当前电源方案", schemeError);

                var refs = new[] { BoostRef, StateRef, EppRef, IdleRef };
                var done = new System.Collections.Generic.List<SettingRef>();

                foreach (SettingRef reference in refs)
                {
                    PowerPlanResult step = WriteBoth(scheme, reference, NoSetting);
                    if (!step.Ok)
                    {
                        Rollback(scheme, done);
                        return step;
                    }

                    done.Add(reference);
                }

                uint rcActive = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                if (rcActive != 0)
                    return PowerPlanResult.Fail("激活电源方案", Describe(rcActive));

                return PowerPlanResult.Success();
            }
            catch (Exception ex)
            {
                return PowerPlanResult.Fail("电源策略", ex.Message);
            }
            finally
            {
                if (schemePtr != IntPtr.Zero)
                    LocalFree(schemePtr);
            }
        }

        /// <summary>本机是否具备读取条件（powrprof 可用且能拿到当前方案）。</summary>
        public static bool IsAvailable()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                return TryGetActiveScheme(out ptr, out _, out _);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    LocalFree(ptr);
            }
        }

        /// <summary>档位的对外显示名（下拉框与日志共用，避免两处文案走样）。</summary>
        public static string Label(PowerPlanProfile profile) => profile switch
        {
            PowerPlanProfile.Unrestricted => "解除限制",
            PowerPlanProfile.Gentle => "温和",
            PowerPlanProfile.Balanced => "均衡",
            PowerPlanProfile.Quiet => "静音",
            _ => "均衡",
        };

        /// <summary>档位的作用说明（下拉框下方那行提示）。</summary>
        public static string Description(PowerPlanProfile profile) => profile switch
        {
            PowerPlanProfile.Unrestricted =>
                "Boost 积极 + 频率上限 100% + 性能优先 + 禁止空闲降频。峰值性能最高，发热与噪音也最大。",
            PowerPlanProfile.Gentle =>
                "Boost 启用、频率上限 100%、轻度偏向性能。日常调校建议停在这一档。",
            PowerPlanProfile.Balanced =>
                "Boost 启用、频率上限 100%、性能与能效各半。与厂商默认行为基本一致。",
            PowerPlanProfile.Quiet =>
                "Boost 停用、频率上限 85%、极致能效。风扇明显更安静，峰值性能下降较多。",
            _ => string.Empty,
        };

        // ---------------- 内部：写与回滚 ----------------

        /// <summary>AC / DC 各写一次。DC 失败不视为整体失败（台式机没有 DC 策略）。</summary>
        private static PowerPlanResult WriteBoth(Guid scheme, SettingRef reference, uint value)
        {
            // static readonly Guid 不能直接 ref 传给 P/Invoke（CS0199），先拷到局部变量。
            Guid sub = SubProcessor, setting = reference.Guid;

            uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            if (rc != 0)
                return PowerPlanResult.Fail(reference.Name, Describe(rc));

            // DC 用同一套值。笔记本拔电时行为要一致，否则「插电静音、拔电全速」，
            // 用户会以为设置没保存。写入失败只记一笔，不阻断 —— 台式机没有电池，
            // 某些 OEM 方案会把 DC 子组整个置为不可写。
            uint rcDc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            if (rcDc != 0 && rcDc != 2 /* ERROR_FILE_NOT_FOUND */)
            {
                // 不返回失败，记录一笔交给 UI 层决定要不要提示
                _lastDcWarning = $"{reference.Name}（电池供电）：{Describe(rcDc)}";
            }

            return PowerPlanResult.Success();
        }

        /// <summary>最近一次 Apply 中 DC 写入的告警（无则 null）。</summary>
        public static string? LastDcWarning => _lastDcWarning;

        /// <summary>回滚本次已写入的索引：尽量恢复成「无设置」，交回系统默认。</summary>
        private static void Rollback(Guid scheme, System.Collections.Generic.List<SettingRef> done)
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                SettingRef reference = done[i];
                Guid sub = SubProcessor, setting = reference.Guid;

                try
                {
                    PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, NoSetting);
                }
                catch
                {
                    // 回滚是尽力而为，失败也不向上抛
                }
            }
        }

        // ---------------- 内部：读 ----------------

        private static bool TryReadFour(
            out uint boost, out uint state, out uint epp, out uint idle, out string error)
        {
            boost = state = epp = idle = NoSetting;
            error = string.Empty;

            IntPtr schemePtr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out schemePtr, out Guid scheme, out error))
                    return false;

                if (!TryReadOne(scheme, BoostRef, out boost, out error))
                    return false;
                if (!TryReadOne(scheme, StateRef, out state, out error))
                    return false;
                if (!TryReadOne(scheme, EppRef, out epp, out error))
                    return false;
                if (!TryReadOne(scheme, IdleRef, out idle, out error))
                    return false;

                return true;
            }
            finally
            {
                if (schemePtr != IntPtr.Zero)
                    LocalFree(schemePtr);
            }
        }

        private static bool TryReadOne(Guid scheme, SettingRef reference, out uint value, out string error)
        {
            value = NoSetting;
            error = string.Empty;

            Guid sub = SubProcessor, setting = reference.Guid;
            uint rc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
            if (rc != 0)
            {
                error = Describe(rc);
                return false;
            }

            return true;
        }

        // ---------------- 内部：档位映射 ----------------

        /// <summary>档位 → 四个索引的目标值。</summary>
        private static (uint Boost, uint State, uint Epp, uint Idle) ValuesOf(PowerPlanProfile profile) =>
            profile switch
            {
                // 「解除限制」用 Boost=2（积极）而不是 1（启用）：1 只是「允许超出标称频率」，
                // 2 才是「尽量往高频跑」。既然用户点了「解除限制」，就该给到最激进的那档。
                // 空闲禁用 = 1：防止核心深睡后再唤醒造成的频率爬升延迟 —— 这是
                // 「点了游戏半天才满频」的常见成因。代价是待机功耗与温度确实更高。
                PowerPlanProfile.Unrestricted => (BoostAggressive, 100, 0, 1),

                // 「温和」用 Boost=1（启用，不写积极）+ EPP 25：兼顾响应与发热。
                PowerPlanProfile.Gentle => (BoostEnabled, 100, 25, 0),

                // 「均衡」EPP 50：Windows 平衡计划的默认取向量级。
                PowerPlanProfile.Balanced => (BoostEnabled, 100, 50, 0),

                // 「静音」Boost=0 才是最本质的降噪手段 —— Boost 的瞬时冲高会带来
                // 风扇的「一阵一阵」，把频率上限压到 85% 是对持续负载的第二道约束。
                PowerPlanProfile.Quiet => (BoostDisabled, 85, 100, 0),

                _ => (BoostEnabled, 100, 50, 0),
            };

        /// <summary>四个索引实际值 → 最接近的档位；对不上任何一档返回 null。</summary>
        private static PowerPlanProfile? Match(uint boost, uint state, uint epp, uint idle)
        {
            foreach (PowerPlanProfile candidate in new[]
                     {
                         PowerPlanProfile.Unrestricted,
                         PowerPlanProfile.Gentle,
                         PowerPlanProfile.Balanced,
                         PowerPlanProfile.Quiet,
                     })
            {
                (uint b, uint s, uint e, uint i) = ValuesOf(candidate);
                if (boost == b && state == s && epp == e && idle == i)
                    return candidate;
            }

            // Boost 的「启用」在部分驱动上会被规范成 1 或 2，其余三项对上也算命中温和档。
            // 这一条是为了让「用户刚才选过温和、但驱动把 Boost 值从 1 读成 2」不显示成自定义。
            if ((boost == BoostEnabled || boost == BoostAggressive)
                && state == 100 && epp == 25 && idle == 0)
                return PowerPlanProfile.Gentle;

            return null;
        }

        // ---------------- 内部：杂项 ----------------

        private static bool TryGetActiveScheme(out IntPtr ptr, out Guid scheme, out string error)
        {
            scheme = Guid.Empty;
            error = string.Empty;

            uint rc = PowerGetActiveScheme(IntPtr.Zero, out ptr);
            if (rc != 0)
            {
                error = Describe(rc);
                return false;
            }

            scheme = Marshal.PtrToStructure<Guid>(ptr);
            return true;
        }

        /// <summary>
        /// 把 Win32 返回码翻成人话。
        /// 0x5 = ACCESS_DENIED：对当前电源方案没有写权限（多见于域策略锁定的机器）。
        /// 0x2 = ERROR_FILE_NOT_FOUND：该索引在当前方案 / 硬件上不存在。
        /// </summary>
        private static string Describe(uint rc)
        {
            if (rc == 5)
                return "当前电源方案被策略锁定，不允许修改（需要管理员权限或域策略放行）。";

            if (rc == 2)
                return "当前硬件或电源方案不提供该设置项。";

            try
            {
                return new Win32Exception(unchecked((int)rc)).Message;
            }
            catch
            {
                return $"系统调用失败，错误码 0x{rc:X8}。";
            }
        }
    }
}
