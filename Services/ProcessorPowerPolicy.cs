using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZenForge.Services
{
    /// <summary>
    /// PBO 策略的可用取值。
    ///
    /// 【这不是 AMD SMU 固件层的 PBO】
    /// SMU 层的 PBO（功耗墙 / 曲线优化 / Boost 上限）没有任何公开接口，
    /// 只有主板 BIOS 与 AMD 驱动能写。硬发 SMU 消息号属于拿用户机器赌博：
    /// 消息号或载荷错一个字节就是黑屏 + 清 CMOS 才能恢复。
    ///
    /// 本服务操作的是 Windows 电源策略层的「处理器性能提升模式」
    /// （GUID_PROCESSOR_PERF_BOOST_MODE）。它同样决定处理器能否超出标称频率运行，
    /// 用系统电源 API 可读可写、有返回码、可一键还原，失败最多是「设置没生效」。
    /// </summary>
    public enum BoostPolicy
    {
        /// <summary>不显式设置，交回 Windows 默认（清除 AC / DC 两个索引）。</summary>
        Auto,

        /// <summary>处理器性能提升模式 = 已启用。</summary>
        Enabled,

        /// <summary>处理器性能提升模式 = 已停用。</summary>
        Disabled,
    }

    /// <summary>
    /// 读写 Windows 电源策略中的「处理器性能提升模式」。
    ///
    /// 全部 P/Invoke 都包在 try/catch 里，任何失败只返回 false 并带上错误文本，
    /// 绝不向上抛 —— 调校页不该因为一个可选策略读不到就整页报错。
    /// </summary>
    public static class ProcessorPowerPolicy
    {
        // ---------------- 电源管理 GUID ----------------

        /// <summary>处理器电源管理子组，所有处理器相关设置都在它下面。</summary>
        private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>处理器性能提升模式（PERFBOOSTMODE）。</summary>
        private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

        // 电源设置索引的取值
        private const uint AccessAc = 0;
        private const uint AccessDc = 1;

        // PROCESSOR_PERF_BOOST_MODE 的合法值（0..6），0 = 已停用，1 = 已启用。
        // 其余值是 OEM 扩展档位（积极 / 节能等），本项目只暴露最明确的两档。
        private const uint BoostDisabled = 0;
        private const uint BoostEnabled = 1;

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerReadACValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, out uint valueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerReadDCValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, out uint valueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerWriteACValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, uint valueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerWriteDCValueIndex(
            IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, uint valueIndex);

        /// <summary>把当前方案重新激活一次，让刚写入的索引立刻生效。</summary>
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        // ---------------- 对外接口 ----------------

        /// <summary>
        /// 读取当前生效电源方案里的处理器性能提升模式。
        /// 方案未被显式设置（即系统默认）时返回 <see cref="BoostPolicy.Auto"/>。
        /// </summary>
        public static bool TryRead(out BoostPolicy policy, out string error)
        {
            policy = BoostPolicy.Auto;
            error = string.Empty;

            IntPtr schemePtr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out schemePtr, out Guid scheme, out error))
                    return false;

                // static readonly 字段不能直接 ref 传给 P/Invoke（CS0199），
                // 先拷到局部变量再取址。两个 GUID 都是固定的，拷贝开销可忽略。
                Guid sub = SubProcessor, setting = PerfBoostMode;
                uint rcAc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint ac);
                if (rcAc != 0)
                {
                    error = Describe(rcAc);
                    return false;
                }

                if (ac == BoostDisabled)
                    policy = BoostPolicy.Disabled;
                else if (ac == BoostEnabled)
                    policy = BoostPolicy.Enabled;
                else if (ac == uint.MaxValue || ac == unchecked((uint)-1))
                    policy = BoostPolicy.Auto;
                else
                    // OEM 扩展档位（积极 / 节能已启用等）。不假装认识，按 Auto 呈现，
                    // 用户一旦选择 Enabled / Disabled 就会被覆盖成明确值。
                    policy = BoostPolicy.Auto;

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (schemePtr != IntPtr.Zero)
                    LocalFree(schemePtr);
            }
        }

        /// <summary>
        /// 写入处理器性能提升模式（AC 与 DC 同时写）。
        /// <see cref="BoostPolicy.Auto"/> 会清除该子组索引，交回系统默认。
        /// </summary>
        public static bool TryWrite(BoostPolicy policy, out string error)
        {
            error = string.Empty;

            IntPtr schemePtr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out schemePtr, out Guid scheme, out error))
                    return false;

                uint value = policy switch
                {
                    BoostPolicy.Enabled => BoostEnabled,
                    BoostPolicy.Disabled => BoostDisabled,
                    // Auto：0xFFFFFFFF 是 Windows 约定的「无设置」，等价于交回默认。
                    // 用 1 或 0 都会变成「显式设置」，与 Auto 的语义不符。
                    _ => uint.MaxValue,
                };

                Guid sub = SubProcessor, setting = PerfBoostMode;
                uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
                if (rc != 0)
                {
                    error = Describe(rc);
                    return false;
                }

                rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
                if (rc != 0)
                {
                    error = Describe(rc);
                    return false;
                }

                // 不重新激活的话，部分系统上写入的索引要等下次切换电源方案才生效，
                // 用户会以为「点了没反应」。
                uint rcActive = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                if (rcActive != 0)
                {
                    error = Describe(rcActive);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
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

        // ---------------- 内部 ----------------

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
        /// </summary>
        private static string Describe(uint rc)
        {
            if (rc == 5)
                return "当前电源方案被策略锁定，不允许修改（需要管理员权限或域策略放行）。";

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
