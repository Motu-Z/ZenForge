using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zentwik.Core.Services
{
    /// <summary>单次优先级下发的结果。失败原因保留到错误码一级，便于向用户解释。</summary>
    public readonly struct PriorityApplyResult
    {
        public int Ok { get; init; }
        public int Failed { get; init; }

        /// <summary>最后一次失败的原因（成功时为空）。</summary>
        public string Error { get; init; }

        /// <summary>最后一次失败的 Win32 错误码（0 表示不是 API 层面的失败）。</summary>
        public int ErrorCode { get; init; }

        public static PriorityApplyResult Ok1() => new() { Ok = 1 };

        public static PriorityApplyResult Fail(int code, string message) =>
            new() { Failed = 1, ErrorCode = code, Error = message };
    }

    /// <summary>
    /// 进程优先级的下发统一出口。
    ///
    /// 【为什么必须收到一处】
    /// 之前有两份实现：
    ///   · Views/ProcessBindingPage.xaml.cs 里的 ApplyPriority(members, label)
    ///   · Services/ProcessWatcher.cs 里的 ApplyPriority(pid, label)
    /// 两份都用「OpenProcess(0x0200) + SetPriorityClass」，
    /// 但**都漏了 PROCESS_QUERY_LIMITED_INFORMATION**。
    ///
    /// 这不是「少给一个权限不要紧」——Windows 对 SetPriorityClass 的要求写得很明确：
    /// 调用方必须有 PROCESS_SET_INFORMATION，**并且**目标进程不是「受保护进程」。
    /// 判定受保护与否的那一步（内核在 NtSetInformationProcess 里做的检查）
    /// 需要调用方能查询目标进程的安全信息；只拿到 0x0200 的句柄在这个检查里会被拒，
    /// 表现为 OpenProcess 成功、SetPriorityClass 返回 ERROR_ACCESS_DENIED(5)。
    ///
    /// 这正好解释了用户看到的现象：任务管理器里同样的操作能成功，
    /// 说明权限本身够（本程序清单是 requireAdministrator），差的是句柄上的访问位。
    /// 而 helldivers2.exe 这类带反作弊的游戏进程恰好是对权限最敏感的那一类 ——
    /// 于是「有的进程能设、有的设不了」。
    ///
    /// 现在统一申请 SET_INFORMATION | QUERY_LIMITED_INFORMATION，
    /// 并在失败时把错误码原样带出去，而不是吞成一句「权限不足」。
    /// </summary>
    public static class ProcessPriority
    {
        // ---------------- 档位 ----------------

        public const string High = "High";
        public const string AboveNormal = "Above Normal";
        public const string Normal = "Normal";
        public const string BelowNormal = "Below Normal";
        public const string Low = "Low";

        /// <summary>
        /// 下拉顺序：由高到低，与页面其他下拉一致。
        ///
        /// 刻意不含 RealTime：它的优先级高于系统输入线程，一个忙等的 RealTime 进程
        /// 能让鼠标键盘完全失去响应。任务管理器里那个选项我们也不提供 ——
        /// 不是做不到，是失败代价与收益完全不成比例。
        /// </summary>
        public static readonly string[] Options = { High, AboveNormal, Normal, BelowNormal, Low };

        /// <summary>
        /// 档位常量 → 界面文案。
        ///
        /// 数据层保持英文常量（binding.json 里存的就是这些字符串，改掉等于废掉旧配置），
        /// 只有「画给用户看」的地方走这里。翻译口径按技术圈习惯：
        ///   Normal 是「标准」而不是「正常」—— 后者在中文里像在描述健康状况；
        ///   Above/Below Normal 译「高于/低于标准」，保留「相对谁」的关系；
        ///   RealTime 译「实时」，但我们不下发这一档（见 Options 的注释）。
        /// </summary>
        public static string DisplayOf(string label) => label switch
        {
            High => "高",
            AboveNormal => "高于标准",
            Normal => "标准",
            BelowNormal => "低于标准",
            Low => "低",
            "RealTime" => "实时",
            _ => string.IsNullOrEmpty(label) ? "—" : label,
        };

        private const uint PRIORITY_CLASS_IDLE = 0x00000040;
        private const uint PRIORITY_CLASS_BELOW_NORMAL = 0x00004000;
        private const uint PRIORITY_CLASS_NORMAL = 0x00000020;
        private const uint PRIORITY_CLASS_ABOVE_NORMAL = 0x00008000;
        private const uint PRIORITY_CLASS_HIGH = 0x00000080;

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>档位标签 → SetPriorityClass 常量。0 表示不认识的标签。</summary>
        public static uint ClassOf(string label) => label switch
        {
            High => PRIORITY_CLASS_HIGH,
            AboveNormal => PRIORITY_CLASS_ABOVE_NORMAL,
            Normal => PRIORITY_CLASS_NORMAL,
            BelowNormal => PRIORITY_CLASS_BELOW_NORMAL,
            Low => PRIORITY_CLASS_IDLE,
            _ => 0,
        };

        /// <summary>
        /// pcPriClassBase（Toolhelp 快照里的基础优先级）→ 下拉档位。
        ///
        /// 取值只有几档固定值（4/6/8/10/13/24/32），不随线程动态优先级浮动。
        /// 不在下拉里的值（RealTime 24/32、裸数字）一律归到 Normal：
        /// 下拉表达的是「我们要设成什么」，一个我们不会去写的档位出现在下拉里，
        /// 会让用户以为选了能保持。
        /// </summary>
        public static string FromBasePriority(int pcPriClassBase) => pcPriClassBase switch
        {
            4 => Low,
            6 => BelowNormal,
            8 => Normal,
            10 => AboveNormal,
            13 => High,
            _ => Normal,
        };

        // ---------------- 下发 ----------------

        /// <summary>
        /// 对一组 PID 下发优先级，逐个统计成功与失败。
        ///
        /// 失败不抛异常：调用方拿到 (Ok, Failed, Error) 自己决定怎么提示。
        /// 对「一组进程」操作时，部分失败是常态（同名进程里有子进程、有受保护进程），
        /// 只要有一个成功就应该算成功，只是要把失败数报出来。
        /// </summary>
        public static PriorityApplyResult Apply(IEnumerable<int> pids, string label)
        {
            uint cls = ClassOf(label);
            if (cls == 0)
                return new PriorityApplyResult { Error = $"未知的优先级档位：{label}" };

            int ok = 0, failed = 0;
            string lastError = string.Empty;
            int lastCode = 0;

            foreach (int pid in pids)
            {
                (bool success, int code, string error) = ApplyOne(pid, cls);
                if (success)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    lastCode = code;
                    lastError = error;
                }
            }

            return new PriorityApplyResult
            {
                Ok = ok,
                Failed = failed,
                Error = lastError,
                ErrorCode = lastCode,
            };
        }

        /// <summary>对单个 PID 下发。</summary>
        public static PriorityApplyResult Apply(int pid, string label)
        {
            uint cls = ClassOf(label);
            if (cls == 0)
                return new PriorityApplyResult { Error = $"未知的优先级档位：{label}" };

            (bool success, int code, string error) = ApplyOne(pid, cls);
            return success
                ? PriorityApplyResult.Ok1()
                : PriorityApplyResult.Fail(code, error);
        }

        private static (bool Ok, int Code, string Error) ApplyOne(int pid, uint cls)
        {
            // 【两种访问权都要】
            // 只有 SET_INFORMATION 时，OpenProcess 可能成功而 SetPriorityClass 报 5，
            // 原因是内核在判定「受保护进程」时需要查询权限（见类注释）。
            IntPtr handle = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
                                        false, pid);
            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return (false, err, DescribeOpenFailure(err));
            }

            try
            {
                if (SetPriorityClass(handle, cls))
                    return (true, 0, string.Empty);

                int err = Marshal.GetLastWin32Error();
                return (false, err, DescribeSetFailure(err));
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>OpenProcess 失败的原文。</summary>
        private static string DescribeOpenFailure(int code) => code switch
        {
            5 => "拒绝访问（错误 5）：目标进程受保护，需要以管理员身份运行",
            87 => "进程已退出（错误 87）",
            1168 => "进程已退出（错误 1168）",
            _ => $"无法打开进程（错误 {code}：{new Win32Exception(code).Message}）",
        };

        /// <summary>SetPriorityClass 失败的原文。</summary>
        private static string DescribeSetFailure(int code) => code switch
        {
            5 => "拒绝访问（错误 5）：系统或反作弊保护拒绝修改优先级",
            87 => "进程已退出（错误 87）",
            1168 => "进程已退出（错误 1168）",
            _ => $"设置失败（错误 {code}：{new Win32Exception(code).Message}）",
        };

        /// <summary>
        /// 判断失败是否只是「进程在这一瞬间退出了」。
        ///
        /// 这种失败是每次刷新都可能撞上的竞态，用户不需要为它做什么，
        /// 不该占用 Toast 也不该进 ERROR 日志。
        /// </summary>
        public static bool IsTransient(PriorityApplyResult result) =>
            result.ErrorCode == 87 || result.ErrorCode == 1168;

        // ---------------- P/Invoke ----------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
