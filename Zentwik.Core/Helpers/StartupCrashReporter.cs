using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 启动期崩溃的最后一根救命稻草。
    ///
    /// 【它必须和 DI 容器完全解耦】
    /// 崩溃点很可能就在建容器、解析路径、加载原生库这几步 —— 也就是日志服务
    /// 本身还没活过来的时候。所以这里不碰 ILogService、不碰 AppPaths，
    /// 只用裸 File IO 与 %AppData% 的字面路径，保证「能在任何时刻写出一个文件」。
    ///
    /// 【为什么不用 MessageBox 的托管封装】
    /// 托管封装要求 UI 框架已经加载完毕，而启动期崩溃时正是 UI 尚未可用。
    /// 直接调 user32 的 MessageBoxW 是唯一在所有阶段都成立的选择，
    /// 它也顺便避免了把 WPF / WinUI 类型拖进纯后台路径。
    /// </summary>
    public static partial class StartupCrashReporter
    {
        private const string LogFileName = "startup_crash.log";

        /// <summary>
        /// 记录异常并返回落盘路径。
        /// </summary>
        /// <param name="exception">捕获到的异常。</param>
        /// <param name="interactive">
        /// 是否弹出原生对话框。后台（silent）模式必须传 false ——
        /// 开机自启时弹一个没人看的框，等于把进程永久挂在桌面上。
        /// </param>
        public static string Report(Exception exception, bool interactive)
        {
            ArgumentNullException.ThrowIfNull(exception);

            string path = BuildPath();

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // 追加而不是覆盖：用户如果连崩两次，第一次的现场不应该被第二次冲掉。
                File.AppendAllText(path, Compose(exception), Encoding.UTF8);
            }
            catch
            {
                // 日志写不出来时唯一还能做的事是不再抛 —— 否则真正的崩溃原因
                // 会被「写日志时的崩溃」顶掉。
            }

            if (interactive)
                ShowNativeDialog(path, exception);

            return path;
        }

        private static string BuildPath()
        {
            // 注意：这里不能用 IAppPaths。AppPaths 的构造会做三级目录迁移，
            // 而崩溃很可能就发生在迁移里 —— 用它取路径会二次抛异常。
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Zentwik");

            return Path.Combine(root, LogFileName);
        }

        private static string Compose(Exception exception)
        {
            StringBuilder sb = new();

            sb.AppendLine("================================================================");
            sb.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] 启动失败");
            sb.AppendLine($"进程：{Environment.ProcessPath ?? "<unknown>"}");
            sb.AppendLine($"命令行：{Environment.CommandLine}");
            sb.AppendLine($"运行时：{RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"系统：{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"基目录：{AppContext.BaseDirectory}");
            sb.AppendLine($"单文件解包目录：{Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? "<未设置，按默认 %TEMP%\\.net>"}");
            sb.AppendLine("----------------------------------------------------------------");

            // ToString() 带 InnerException 链与全部堆栈，比 ex.Message 有用一个数量级。
            sb.AppendLine(exception.ToString());
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// 原生对话框。不用托管封装：启动期崩溃时 UI 框架往往还没加载完，
        /// 只有 user32 的 MessageBoxW 在所有阶段都成立。
        /// </summary>
        private static void ShowNativeDialog(string path, Exception exception)
        {
            try
            {
                string message =
                    "Zentwik 启动失败，进程已退出。\n\n" +
                    $"异常：{exception.GetType().Name}: {exception.Message}\n\n" +
                    $"完整堆栈已写入：\n{path}";

                MessageBoxW(IntPtr.Zero, message, "Zentwik 启动失败", MB_OK | MB_ICONERROR);
            }
            catch
            {
                // 对话框也失败就算了，日志已经落盘。
            }
        }

        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONERROR = 0x00000010;

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
