using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZenForge.Services
{
    /// <summary>
    /// 处理器电源管理里两个「单独暴露」的索引：升频模式与核心停放。
    ///
    /// 【为什么和 ProcessorPowerPolicy 分成两个类】
    /// 那边的四个索引是一组「方案档位」，只能整套切换，用户选的是「解除限制 / 静音」，
    /// 不是某个具体数值。这两个是独立旋钮：用户想单独把升频从「积极」改成「高效」，
    /// 或者把停放的核心数从 100% 拉到 50%，不应该被迫换一个档位。
    /// 拆开后两边互不覆盖 —— 应用档位不会把用户手调的停放比例冲掉。
    ///
    /// 【和 powercfg 命令行的对应关系】
    /// 两者都在「处理器电源管理」子组 54533251-82be-4824-96c1-47b60b740d00 下，
    /// 用 `powercfg /q SCHEME_CURRENT SUB_PROCESSOR &lt;GUID&gt;` 能看到同一批数字。
    /// 这里直接走 powrprof，省掉进程启动开销，也不用解析命令行输出。
    ///
    /// PERFBOOSTMODE（升频模式）be337238-0d82-4146-a960-4f3749d470c7
    ///   数值不是「档位序号」，而是 **位掩码**：
    ///     0 = 已停用
    ///     1 = 已启用（允许超过标称频率）
    ///     2 = 积极（Enabled | Aggressive，尽量往上跑）
    ///     3 = 高效（Enabled | Efficient，能效优先的升频）
    ///     4 = 积极且高效（2 | 4，两个位一起置）
    ///   注意 4 不是「保留值」—— 它是位组合结果，Windows 里确有这一项，
    ///   部分 OEM 方案会把它作为默认值。按「4 是保留值」去写，会在读回 4 时
    ///   误判成「不认识的值」，界面上就会冒出一个莫名其妙的「未知（4）」。
    ///
    ///   【这里说的是位掩码字段，不是界面上的存储格式】
    ///   powercfg /q 与注册表 Attributes 里，这个索引以枚举形式存储（0/1/2/3/4），
    ///   不是二进制标志位。上面这张表描述的是「有效值之间的组合关系」，
    ///   不代表可以直接拿 2 | 4 = 6 写进去 —— 6 不是合法值，Windows 会静默忽略。
    ///
    /// CPMINMAX（核心停放）0cc5b647-c1df-4637-891a-dec35c318583
    ///   这是个「范围型」索引，Windows 在注册表里存成三条值：
    ///     Attributes = 1（范围型）
    ///     Min        = 最小可停放比例（%）
    ///     Max        = 最大可停放比例（%）
    ///   界面上给一枚滑杆同时控制两者（同一个值写进 Min/Max），
    ///   也就是「停放上限」。为什么不做成两条独立滑杆：Min &gt; Max 是非法状态，
    ///   Windows 遇到会静默钳制，用户看到自己刚拖的值跳回去，反而困惑。
    /// </summary>
    public static class ProcessorPowerTuning
    {
        // ---------------- 电源管理 GUID ----------------

        /// <summary>处理器电源管理子组。</summary>
        private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>处理器性能提升模式（升频模式）。</summary>
        private static readonly Guid PerfBoostGuid = new("be337238-0d82-4146-a960-4f3749d470c7");

        /// <summary>处理器性能核心停放最小核心（范围型索引）。</summary>
        private static readonly Guid CoreParkingGuid = new("0cc5b647-c1df-4637-891a-dec35c318583");

        /// <summary>范围型索引的 Attributes 值。非 1 的项不接受 Min/Max 二元组。</summary>
        private const uint RangeAttributes = 1;

        /// <summary>「无设置」哨兵值：写入后 Windows 回落方案默认。</summary>
        private const uint NoSetting = uint.MaxValue;

        // ---------------- P/Invoke ----------------

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerReadACValueIndex(
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

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        // ---------------- 升频模式 ----------------

        /// <summary>
        /// 升频模式的可选项。数值即为写入 powercfg 的枚举值，见类注释。
        /// 刻意不标 [Flags]：它是枚举存储，不是位集合，标了会诱导调用方去按位组合。
        /// </summary>
        public enum BoostMode : uint
        {
            Disabled = 0,
            Enabled = 1,
            Aggressive = 2,
            Efficient = 3,
            AggressiveEfficient = 4,
        }

        /// <summary>
        /// 升频模式下拉框的顺序与文案。
        ///
        /// 顺序按「激进度由高到低」排，和调校页其他下拉（档位、优先级）一致：
        /// 用户扫一眼就能判断"往上选更猛"，不用逐个读文字。
        /// 文案用 Windows 设置里那套中文，去控制面板能一一对上。
        /// </summary>
        public static readonly (BoostMode Mode, string Label, string Hint)[] BoostOptions =
        {
            (BoostMode.AggressiveEfficient, "积极且高效", "积极升频的同时兼顾能效，部分 OEM 方案的默认值"),
            (BoostMode.Aggressive,          "积极",       "允许超出标称频率并尽量维持在高频"),
            (BoostMode.Enabled,             "已启用",     "允许超出标称频率，但由系统自行决定何时升频"),
            (BoostMode.Efficient,           "高效",       "升频偏向能效，冲高更保守"),
            (BoostMode.Disabled,            "已停用",     "锁在标称频率，不升频"),
        };

        /// <summary>
        /// 读当前升频模式。
        /// 返回 false 表示这台机器 / 这个方案不提供该索引（多见于虚拟机与部分 OEM 精简方案）。
        /// value 返回 NoSetting 说明索引存在但没有设定值，此时显示「系统默认」。
        /// </summary>
        public static bool TryReadBoost(out uint value, out string error)
        {
            value = NoSetting;
            error = string.Empty;

            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out ptr, out Guid scheme, out error))
                    return false;

                Guid sub = SubProcessor, setting = PerfBoostGuid;
                uint rc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
                if (rc != 0)
                {
                    error = Describe(rc);
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
                if (ptr != IntPtr.Zero)
                    LocalFree(ptr);
            }
        }

        /// <summary>写升频模式（AC + DC）。DC 失败不阻断，与 ProcessorPowerPolicy 的约定一致。</summary>
        public static PowerPlanResult WriteBoost(BoostMode mode)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (!TryGetActiveScheme(out ptr, out Guid scheme, out string error))
                    return PowerPlanResult.Fail("当前电源方案", error);

                Guid sub = SubProcessor, setting = PerfBoostGuid;
                uint raw = (uint)mode;

                uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, raw);
                if (rc != 0)
                    return PowerPlanResult.Fail("升频模式", Describe(rc));

                // 拔电时行为要一致。失败不管 —— 台式机没有电池，某些 OEM 方案也不给写 DC。
                PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, raw);

                uint rcActive = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                if (rcActive != 0)
                    return PowerPlanResult.Fail("激活电源方案", Describe(rcActive));

                return PowerPlanResult.Success();
            }
            catch (Exception ex)
            {
                return PowerPlanResult.Fail("升频模式", ex.Message);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    LocalFree(ptr);
            }
        }

        /// <summary>位掩码 → 可读文案。遇到既有位以外的新值如实显示数值，不猜。</summary>
        public static string BoostLabel(uint raw)
        {
            if (raw == NoSetting)
                return "系统默认";

            foreach ((BoostMode mode, string label, _) in BoostOptions)
            {
                if ((uint)mode == raw)
                    return label;
            }

            return $"未知（{raw}）";
        }

        // ---------------- 核心停放 ----------------

        /// <summary>
        /// 读核心停放上限（%）。
        ///
        /// 【为什么读的是注册表而不是 PowerReadACValueIndex】
        /// 核心停放是范围型索引，PowerReadACValueIndex 读到的是「当前生效值」，
        /// 而我们要显示的是用户设定的上限 —— 两者在某些场景下不等
        /// （例如系统正处在节能状态时，实际停放比例会高于设定值）。
        /// 范围型索引的 Min/Max 存在注册表
        ///   HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\&lt;子组&gt;\&lt;索引&gt;
        /// 下，是唯一能拿到「用户设定值」的位置。
        ///
        /// 加 NOTHREADS 打开：读电源设置不需要事务隔离，走普通读即可，
        /// 也避免在只读场景下拿到写锁去和 powercfg 争。
        /// </summary>
        public static bool TryReadCoreParking(out uint percent, out string error)
        {
            percent = NoSetting;
            error = string.Empty;

            try
            {
                using Microsoft.Win32.RegistryKey? key =
                    Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RangeKeyPath(CoreParkingGuid), false);

                if (key == null)
                {
                    // 索引不存在不是错误 —— 这台机器 / 这个方案没暴露该项
                    error = "当前电源方案不提供核心停放设置。";
                    return false;
                }

                if (!TryReadDword(key, "Attributes", out uint attributes) || attributes != RangeAttributes)
                {
                    error = "核心停放不是范围型索引，无法按百分比读取。";
                    return false;
                }

                // 滑杆控制的是「上限」，所以取 Max。
                // Min 是「至少保持停放多少」，界面上不暴露，理由见 WriteCoreParking。
                if (!TryReadDword(key, "Max", out uint max))
                {
                    error = "核心停放尚未设定值。";
                    return false;
                }

                percent = max > 100 ? 100 : max;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 写核心停放上限（%）。Min 与 Max 同时写同一个数。
        ///
        /// 【为什么同时写 Min】
        /// 只改 Max 而 Min 停在更大的值上，Windows 会把整个范围判为非法并回落默认，
        /// 用户看到的就是「拖了没反应」。同写同改之后，范围永远合法（Min == Max）。
        /// 代价是「至少停放 N%」这半个能力被放弃了 —— 但那是「强制少用核心」，
        /// 对一个调校工具来说不是用户想要的方向，语义上也容易理解错。
        /// </summary>
        /// <param name="percent">0 = 允许全部核心停下，100 = 禁止停放（全部核心保持唤醒）。</param>
        public static PowerPlanResult WriteCoreParking(uint percent)
        {
            if (percent > 100)
                percent = 100;

            try
            {
                using (Microsoft.Win32.RegistryKey? key =
                       Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RangeKeyPath(CoreParkingGuid), writable: true))
                {
                    if (key == null)
                        return PowerPlanResult.Fail("核心停放", "没有写入权限，或该索引在当前方案中不存在。");

                    // 两个值必须同写：只改 Max 而 Min 更大时，整个范围非法，
                    // Windows 会静默回落默认值。
                    key.SetValue("Min", (int)percent, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("Max", (int)percent, Microsoft.Win32.RegistryValueKind.DWord);
                }

                // 改完必须重新激活方案，否则要等下次切电源计划才生效。
                IntPtr ptr = IntPtr.Zero;
                try
                {
                    if (!TryGetActiveScheme(out ptr, out Guid scheme, out string schemeError))
                        return PowerPlanResult.Fail("当前电源方案", schemeError);

                    uint rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                    if (rc != 0)
                        return PowerPlanResult.Fail("激活电源方案", Describe(rc));
                }
                finally
                {
                    if (ptr != IntPtr.Zero)
                        LocalFree(ptr);
                }

                return PowerPlanResult.Success();
            }
            catch (UnauthorizedAccessException)
            {
                return PowerPlanResult.Fail("核心停放", "没有权限写入电源设置，请以管理员身份运行。");
            }
            catch (Exception ex)
            {
                return PowerPlanResult.Fail("核心停放", ex.Message);
            }
        }

        /// <summary>核心停放的作用说明（滑杆下方那行字，随数值变化）。</summary>
        public static string ParkingHint(uint percent)
        {
            if (percent >= 100)
                return "禁止核心停放：所有核心始终保持唤醒。延迟最低，待机功耗与温度最高。";

            if (percent >= 60)
                return "少量停放：空闲核心会被停掉，负载上来时唤醒有轻微延迟。";

            if (percent >= 20)
                return "允许停放多数空闲核心，功耗明显下降，突发负载的响应略慢。";

            return "最大停放：只留必要核心，待机最省电，但唤醒延迟最明显。";
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

        /// <summary>Win32 返回码 → 人话。与 ProcessorPowerPolicy.Describe 保持同一套措辞。</summary>
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

        // ---------------- 注册表读写 ----------------
        //
        // 用 Microsoft.Win32.Registry 而不是自己 P/Invoke advapi32：
        //   · 项目里虽然到处是 P/Invoke，但那些都是 powrprof / user32 这类
        //     没有托管封装、或封装行为不符合需求的 API；
        //   · Registry 的 RegistryValueKind.DWord 语义明确（32 位小端），
        //     自己封 RegQueryValueEx 要处理 type / size 两个 out 参数，
        //     任何一个填错都会静默读到 0 而不是报错 —— 这类 bug 极难发现。

        /// <summary>范围型索引在注册表里的位置。</summary>
        private static string RangeKeyPath(Guid setting) =>
            $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{SubProcessor:D}\{{{setting:D}}}";

        private static bool TryReadDword(Microsoft.Win32.RegistryKey key, string name, out uint value)
        {
            value = 0;

            object? raw = key.GetValue(name);
            if (raw is not int intValue)
                return false;

            value = unchecked((uint)intValue);
            return true;
        }
    }
}
