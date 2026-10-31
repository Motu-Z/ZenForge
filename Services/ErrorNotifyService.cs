using System;
using Zentwik.Helpers;

namespace Zentwik.Services
{
    /// <summary>
    /// 「出错时通知我」的执行点。
    ///
    /// 设置页那个开关过去只改了一个 <see cref="UiSettings.NotifyOnError"/> 字段，
    /// 没有任何代码读它 —— 开关是断链的，用户打开或关闭都毫无区别。
    /// 这里补上消费端。
    ///
    /// 【三种通道，按「用户此刻在哪」选择】
    ///   1. 主窗口可见  → Toast（右上浮条，3 秒自散）。用户就在界面前，
    ///      不该用系统气泡把他从当前操作里拽出来。
    ///   2. 主窗口隐藏（收进托盘）→ 系统托盘气泡。界面都看不见了，
    ///      再往窗口里塞 Toast 等于不通知。
    ///   3. 托盘也不在（用户关了托盘图标）→ 只写日志，不弹任何东西。
    ///      这是用户自己选的「不要打扰」，尊重它。
    ///
    /// 【这个类不负责什么】
    ///   · 不决定错误要不要写日志：日志是审计，永远写，与开关无关。
    ///   · 不弹模态框：模态属于「必须做决定」的场景（写 SMU 失败、破坏性操作），
    ///     那些地方直接调 DarkDialog，不走这里。本类只做非阻塞提示。
    /// </summary>
    public static class ErrorNotifyService
    {
        /// <summary>
        /// 上报一条非阻塞错误。
        /// </summary>
        /// <param name="detail">写给日志的完整描述（含错误码 / 异常文本）。</param>
        /// <param name="title">气泡标题，也是日志里的来源标记。</param>
        /// <param name="summary">给用户看的一行话（气泡正文）。</param>
        /// <param name="hint">多行说明（仅在没有气泡通道时并入日志）。</param>
        public static void Report(string detail, string title, string summary, string hint = "")
        {
            // 日志永远写：这是审计轨迹，不受任何开关影响。
            try
            {
                Svc.Log.Error(string.IsNullOrEmpty(hint)
                    ? $"{title}：{detail}"
                    : $"{title}：{detail}｜{hint}");
            }
            catch
            {
                // 连日志都写不了就继续往下走，通知本身仍然有价值
            }

            // 用户关掉了「出错时通知我」：到此为止。
            if (!Svc.Ui.Settings.NotifyOnError)
                return;

            try
            {
                if (UiPresence.MainWindowVisible)
                {
                    ToastService.Error(string.IsNullOrEmpty(summary) ? detail : summary);
                    return;
                }

                // 界面不可见：走系统气泡。托盘没建起来时这个方法返回 false，什么都不弹。
                TrayIconService.ShowBalloon(title, summary, detail, isError: true);
            }
            catch (Exception ex)
            {
                // 通知失败绝不反过来影响业务 —— 静默记一笔。
                try { Svc.Log.Warn($"错误通知发送失败：{ex.Message}"); } catch { }
            }
        }
    }
}
