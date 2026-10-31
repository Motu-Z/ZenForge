using System.Runtime.CompilerServices;
using System.Threading;
using Zenwik.Helpers;
using Zenwik.Services;

namespace Zenwik
{
    /// <summary>
    /// 升级失败后的回退宿主。
    ///
    /// 为什么单独一个类型：<see cref="Services.HeadlessHost"/> 必须保持零 WPF 依赖
    /// （IL 扫描会检查这一点，这是「纯后台不加载界面框架」的静态证据）。
    /// 而回退路径需要的恰恰是 WPF 托盘 —— 所以把它挪到这里，
    /// 由 HeadlessHost 通过 <see cref="Run"/> 这个 NoInlining 的入口进入。
    ///
    /// 前提：能走到这里说明 WPF 已经加载成功了（Application 建起来了），
    /// 只是界面没能搭完。此时内存已经回不去，再折腾原生托盘毫无意义 ——
    /// 直接用 WPF 那套 TrayIconService 撑住入口，让绑核继续跑。
    /// </summary>
    internal static class GuiFallback
    {
        /// <summary>
        /// 撑起 WPF 托盘并阻塞，直到用户从托盘选「退出」。
        /// 返回 0 表示正常退出。
        ///
        /// 退出链：托盘「退出」→ <see cref="App.ExitApplication"/> → HardStop()
        /// 收摊（含 TrayIconService.Shutdown）→ Set 本方法的信号 → 这里返回
        /// → HeadlessHost.Cleanup() 补上监听和单实例锁。
        /// 所以这里**不要**再关托盘：那样会在 HardStop 之后再关一次。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Run()
        {
            LogService.Info("升级回退：改用 WPF 托盘常驻（绑核不中断，双击 exe 可重试打开界面）");

            try
            {
                // 单实例锁一直在本进程手里，重新接上唤醒。
                // 必须回到 UI 线程：ShowOrCreateMainWindow 会 new MainWindow()。
                SingleInstance.WakeRequested = () =>
                {
                    System.Windows.Application? app = System.Windows.Application.Current;
                    if (app == null)
                        return;

                    app.Dispatcher.BeginInvoke(
                        new System.Action(TrayIconService.ShowOrCreateMainWindow));
                };

                if (UiSettingsStore.Settings.ShowTrayIcon)
                    TrayIconService.EnsureCreated();

                using ManualResetEventSlim exit = new(false);
                App.BackgroundExitSignal = exit;

                exit.Wait();

                App.BackgroundExitSignal = null;
                return 0;
            }
            catch (System.Exception ex)
            {
                LogService.Error($"升级回退失败，进程退出：{ex}");
                return 1;
            }
        }
    }
}
