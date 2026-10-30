using System;
using ZenForge.Controls;

namespace ZenForge.Services
{
    /// <summary>
    /// 轻量提示服务的静态门面。
    ///
    /// 【什么时候用它，什么时候用 DarkDialog】
    ///
    /// 用 Toast（非阻断，3 秒自散）：
    ///   · 保存配置成功 / 加载配置成功 / 已复制之类「做成了，知道就行」
    ///   · 应用参数成功
    ///   · 主题、强调色已切换
    ///
    /// 用 DarkDialog（模态，必须点确定）：
    ///   · 全局重置（破坏性，需要二次确认）
    ///   · 参数严重越界（需要用户看清楚并决定要不要继续）
    ///   · 写入失败 / 硬件异常（用户必须知道，而且可能要去看日志）
    ///   · 未处理异常兜底
    ///
    /// 判据很简单：**用户必须读完并做决定吗？** 是 → 模态；否 → Toast。
    ///
    /// 【失败降级】
    /// Host 尚未挂上（启动早期、或主窗口已销毁进托盘）时，只写日志，不抛异常。
    /// 提示服务自身绝不能成为崩溃源 —— 为了显示「已保存」而让程序挂掉，
    /// 是比不显示更荒谬的结果。
    /// </summary>
    public static class ToastService
    {
        /// <summary>当前 Toast 宿主。由 MainWindow 里的 ToastHost 在 Loaded 时注入。</summary>
        internal static ToastHost? Host { get; set; }

        public static void Success(string message) => Show(message, ToastKind.Success);

        public static void Info(string message) => Show(message, ToastKind.Info);

        public static void Warning(string message) => Show(message, ToastKind.Warning);

        public static void Error(string message) => Show(message, ToastKind.Error);

        private static void Show(string message, ToastKind kind)
        {
            try
            {
                ToastHost? host = Host;
                if (host != null)
                {
                    host.Show(message, kind);
                    return;
                }
            }
            catch (Exception ex)
            {
                // 走日志，不再往上抛：提示失败不该影响正在进行的业务操作
                try { LogService.Warn($"Toast 显示失败：{ex.Message}"); } catch { }
                return;
            }

            // 宿主不在（无窗口状态）：退化成日志，至少留下痕迹
            try
            {
                LogService.Info($"[提示] {message}");
            }
            catch
            {
                // 连日志都写不了就彻底放弃
            }
        }
    }
}
