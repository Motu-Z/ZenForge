using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Zenwik.Core.Services
{
    /// <summary>
    /// 开机静默自启：通过 Windows 计划任务实现「登录后延时 15 秒、最高权限、无窗口启动」。
    ///
    /// 为什么用 schtasks.exe 而不是 COM（TaskScheduler / ITaskService）：
    ///   1. 零依赖：schtasks 是 Windows 内置命令行，不需要注册 COM 互操作程序集，
    ///      也不受 .NET 与 Windows 版本兼容性影响。
    ///   2. 需要的能力它都覆盖：登录触发器、延时、RunLevel=Highest。
    ///   3. 便于排错：命令行输出可直接进日志，用户也能自己在任务计划程序里核对。
    ///
    /// 为什么用 /XML 而不是 /Create /SC ONLOGON /RL HIGHEST：
    ///   · /XML 能把触发器、主体、设置一次写全，不受 schtasks 参数缺省值影响；
    ///   · 路径带空格时 XML 的 &lt;Command&gt; 是独立元素，不用跟 /TR 的引号规则搏斗。
    ///
    /// 【主体写法是这套方案唯一容易搞错、且错了就完全不触发的地方】
    ///   必须是 UserId + LogonType=InteractiveToken + RunLevel=HighestAvailable：
    ///     · UserId 用当前用户的 SID —— 登录触发器和主体都指向同一个人，
    ///       计划任务服务才能把「该用户登录」这个事件落到这个任务上；
    ///     · InteractiveToken 表示直接用登录会话的令牌，不存密码、不需要 /RP；
    ///     · HighestAvailable 让它以管理员令牌启动，且**不经过 UAC**。
    ///
    ///   绝对不要写成 &lt;GroupId&gt;S-1-5-32-544&lt;/GroupId&gt;（Administrators 组）+ 不指定
    ///   UserId 的 LogonTrigger。组主体代表「一组人」，登录事件无法映射到具体主体，
    ///   表现就是这个任务创建成功、状态正常、但每次开机都不触发，且没有任何报错。
    /// </summary>
    public static class AutoStartService
    {
        /// <summary>计划任务名称（固定，便于用户识别）。</summary>
        public const string TaskName = "ZenwikAutoStart";

        /// <summary>登录后延时秒数。</summary>
        private const int DelaySeconds = 15;

        /// <summary>静默模式启动参数。</summary>
        public const string SilentArgument = "-silent";

        /// <summary>
        /// 读取 schtasks 输出用的编码。
        ///
        /// schtasks 是控制台程序，往管道写的是**系统 OEM 代码页**（中文系统 936），
        /// 用 UTF-8 去读会得到一屏乱码，后面按「状态 / 上次结果」这些标签做的匹配全部落空，
        /// 诊断日志等于白写。这里按 GetOEMCP 取真实代码页。
        /// 注意：.NET Core 起 GBK 等传统代码页必须显式注册 CodePagesEncodingProvider，
        /// 该类库随 Microsoft.NETCore.App 一起发布，不需要额外引用包。
        /// </summary>
        private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

        private static Encoding ResolveConsoleEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                uint codePage = GetOEMCP();
                if (codePage != 0 && codePage != 65001)
                    return Encoding.GetEncoding((int)codePage);
            }
            catch
            {
                // 取不到就退回 UTF-8：乱码总比抛异常好
            }

            return Encoding.UTF8;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        /// <summary>计划任务当前是否已注册。</summary>
        public static bool IsEnabled()
        {
            try
            {
                (int exitCode, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
                return exitCode == 0;
            }
            catch (Exception ex)
            {
                LogService.Warn($"查询计划任务失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建（或覆盖）计划任务。
        /// </summary>
        /// <returns>true = 创建成功且已回查确认存在。</returns>
        public static bool Enable()
        {
            string? exePath = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                LogService.Error("创建计划任务失败：无法定位 Zenwik.exe 路径");
                return false;
            }

            string? userId = ResolveCurrentUserIdentity();
            if (string.IsNullOrEmpty(userId))
            {
                LogService.Error("创建计划任务失败：无法获取当前用户标识");
                return false;
            }

            string? tempXml = null;
            try
            {
                tempXml = Path.Combine(Path.GetTempPath(), $"Zenwik-AutoStart-{Guid.NewGuid():N}.xml");

                string xml = BuildTaskXml(exePath, SilentArgument, userId);
                // UTF-16 + BOM：schtasks /XML 最稳定的编码
                File.WriteAllText(tempXml, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

                // /F 即「存在则覆盖」，不必先 Delete。
                // 先删再建存在一个空窗：建失败时旧任务已经没了，用户只剩一个关着的开关。
                (int exitCode, string output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F");

                if (exitCode != 0)
                {
                    LogService.Error($"创建计划任务失败（退出码 {exitCode}）：{output}");
                    return false;
                }

                // 回查：schtasks 偶有「返回 0 但任务没落地」的情况（多半是权限或策略拦截）
                if (!IsEnabled())
                {
                    LogService.Error($"计划任务 {TaskName} 创建后回查不到，视为失败。原始输出：{output}");
                    return false;
                }

                LogService.Info(
                    $"计划任务 {TaskName} 创建成功：用户 {userId} · 登录后延时 {DelaySeconds} 秒 · 最高权限 · 静默启动 " +
                    $"（{exePath} {SilentArgument}）");

                // 把「下次运行时间 / 上次结果 / 运行方式」写进日志。
                // 用户下次开机发现没起来时，这三个字段直接指出是没触发还是启动后崩了。
                LogTaskState();
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error($"创建计划任务异常：{ex.Message}");
                return false;
            }
            finally
            {
                if (tempXml != null)
                {
                    try
                    {
                        File.Delete(tempXml);
                    }
                    catch
                    {
                        // 临时文件清理失败不影响主流程
                    }
                }
            }
        }

        /// <summary>删除计划任务。</summary>
        /// <returns>true = 已删除或本就不存在。</returns>
        public static bool Disable()
        {
            try
            {
                if (!IsEnabled())
                {
                    LogService.Info($"计划任务 {TaskName} 不存在，无需删除");
                    return true;
                }

                (int exitCode, string output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                if (exitCode == 0)
                {
                    LogService.Info($"计划任务 {TaskName} 已删除（开机静默自启已关闭）");
                    return true;
                }

                LogService.Error($"删除计划任务失败（退出码 {exitCode}）：{output}");
                return false;
            }
            catch (Exception ex)
            {
                LogService.Error($"删除计划任务异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 构造任务定义 XML。
        /// </summary>
        /// <param name="exePath">Zenwik.exe 完整路径。</param>
        /// <param name="arguments">启动参数（-silent）。</param>
        /// <param name="userId">当前用户 SID 或 DOMAIN\User；触发器和主体必须用同一份。</param>
        private static string BuildTaskXml(string exePath, string arguments, string userId)
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            XDocument doc = new(
                new XDeclaration("1.0", "UTF-16", null),
                new XElement(ns + "Task",
                    new XAttribute("version", "1.2"),

                    new XElement(ns + "RegistrationInfo",
                        new XElement(ns + "Author", "Zenwik"),
                        new XElement(ns + "Description",
                            "Zenwik 登录静默自启：延时 15 秒后以最高权限重新应用 CO / FMax 并启动进程绑核监听。")),

                    // 触发器：该用户登录，延时 15 秒。
                    // UserId 必须写 —— 只写 Enabled + Delay 在组主体任务上永远不触发。
                    new XElement(ns + "Triggers",
                        new XElement(ns + "LogonTrigger",
                            new XElement(ns + "Enabled", "true"),
                            new XElement(ns + "UserId", userId),
                            new XElement(ns + "Delay", $"PT{DelaySeconds}S"))),

                    // 主体：当前用户 + 交互令牌 + 最高权限。
                    // InteractiveToken 是不存密码、不弹 UAC 的关键。
                    new XElement(ns + "Principals",
                        new XElement(ns + "Principal",
                            new XAttribute("id", "Author"),
                            new XElement(ns + "UserId", userId),
                            new XElement(ns + "LogonType", "InteractiveToken"),
                            new XElement(ns + "RunLevel", "HighestAvailable"))),

                    // 元素顺序与「任务计划程序」自身导出的格式一致
                    new XElement(ns + "Settings",
                        new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                        new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                        new XElement(ns + "StopIfGoingOnBatteries", "false"),
                        new XElement(ns + "AllowHardTerminate", "true"),
                        // 错过的触发补跑：快速启动（休眠）恢复时，登录事件可能早于任务服务就绪
                        new XElement(ns + "StartWhenAvailable", "true"),
                        new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                        new XElement(ns + "IdleSettings",
                            new XElement(ns + "StopOnIdleEnd", "false"),
                            new XElement(ns + "RestartOnIdle", "false")),
                        new XElement(ns + "AllowStartOnDemand", "true"),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Hidden", "false"),
                        new XElement(ns + "RunOnlyIfIdle", "false"),
                        // 登录触发不需要唤醒机器：WakeToRun 只对时间/空闲触发器有意义
                        new XElement(ns + "WakeToRun", "false"),
                        new XElement(ns + "ExecutionTimeLimit", "PT0S")),         // 不限时长

                    // 动作：静默启动
                    new XElement(ns + "Actions",
                        new XAttribute("Context", "Author"),
                        new XElement(ns + "Exec",
                            new XElement(ns + "Command", exePath),
                            new XElement(ns + "Arguments", arguments)))));
            return doc.Declaration + Environment.NewLine + doc.ToString();
        }

        /// <summary>
        /// 把计划任务的关键状态写进日志：下次运行时间、上次运行时间、上次结果、运行方式。
        /// schtasks 输出是本地化的，因此同时匹配中英文标签。
        /// </summary>
        private static void LogTaskState()
        {
            try
            {
                (int code, string output) = RunSchtasks($"/Query /TN \"{TaskName}\" /V /FO LIST");
                if (code != 0 || string.IsNullOrWhiteSpace(output))
                {
                    LogService.Warn($"读取计划任务 {TaskName} 状态失败（退出码 {code}）");
                    return;
                }

                // 只看排障需要的几行，不把整份 30 行 dump 塞进日志页
                string[] keys =
                {
                    "状态", "Status",
                    "运行方式", "Run As User",
                    "上次运行时间", "Last Run Time",
                    "上次结果", "Last Result",
                    "下次运行时间", "Next Run Time"
                };

                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0)
                        continue;

                    if (keys.Any(k => line.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase)))
                        LogService.Info($"[自启] {line}");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"读取计划任务状态异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 取当前用户标识：优先 SID（与账户改名、域名变更无关），取不到再回退 DOMAIN\User。
        /// </summary>
        private static string? ResolveCurrentUserIdentity()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                string? sid = identity.User?.Value;
                if (!string.IsNullOrWhiteSpace(sid) && sid.StartsWith("S-1-", StringComparison.Ordinal))
                    return sid;
            }
            catch
            {
                // 身份查询失败，走回退路径
            }

            try
            {
                string user = Environment.UserName;
                if (string.IsNullOrWhiteSpace(user))
                    return null;

                string domain = Environment.UserDomainName;
                return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>定位当前可执行文件（含自包含发布与单文件两种情况）。</summary>
        private static string? ResolveExecutablePath()
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;

            try
            {
                path = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            catch
            {
                // 忽略
            }

            return null;
        }

        /// <summary>执行 schtasks 并收集输出（不弹窗、不阻塞 UI）。</summary>
        private static (int ExitCode, string Output) RunSchtasks(string arguments)
        {
            ProcessStartInfo psi = new("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 schtasks.exe");

            // 两个管道必须并发读。先 ReadToEnd(stdout) 再读 stderr 是经典死锁：
            // 若子进程先把 stderr 的 4KB 缓冲区写满，它会阻塞在那里等父进程读，
            // 而父进程正阻塞在等 stdout 关闭——双方互等，直到超时也拿不到退出码。
            // 用异步读先把两路数据抽干，再等退出。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(15000);
            if (!exited)
            {
                // 超时不杀就会残留一个 schtasks 进程，下次再调又是一份
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出或无权 */ }

                // 两个读取任务此刻多半还未完成，Dispose 会中断管道让它们以异常结束。
                // 必须在这里观察掉，否则会变成未观察的 task 异常（可能触发
                // TaskScheduler.UnobservedTaskException，在严格配置下影响进程）。
                ObserveQuietly(stdoutTask);
                ObserveQuietly(stderrTask);

                return (-1, "schtasks 超时（15s）未返回");
            }

            // 进程已退出，剩下的读取会立刻完成；给它一个短上限防止异常挂起
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 2000);
            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";

            string combined = string.Join(" ",
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            return (process.ExitCode, combined);
        }

        /// <summary>
        /// 静默观察一个可能已失败的 Task：吞掉异常，避免变成未观察异常。
        /// </summary>
        private static void ObserveQuietly(Task task)
        {
            try
            {
                task.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                // 观察失败本身无影响
            }
        }
    }
}
