using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Zenwik.Helpers;
using Zenwik.Services;

namespace Zenwik.Views
{
    /// <summary>
    /// 高级页：安全（不可关的两道闸）+ 重置（两个危险操作）。
    ///
    /// 这一页只做「确认 + 调用 + 汇报」，全部动作在 <see cref="FactoryResetService"/> 里。
    /// 破坏性操作保留模态确认：Toast 是单向通知，承载不了「确认 / 取消」。
    /// </summary>
    public partial class AdvancedPage : UserControl
    {
        public const string PageKey = "advanced";

        public AdvancedPage() => InitializeComponent();

        /// <summary>停用全部功能：解除干预，保留配置。</summary>
        private async void DisableAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (!DarkDialog.Confirm(
                    "停用后本程序将不再干预系统与硬件：\n\n" +
                    "· 停止后台调度，解除全部进程的绑核\n" +
                    "· 移除开机自启计划任务\n" +
                    "· 把 CO / FMax 与电源方案写回出厂值\n" +
                    "· 关闭后台服务与托盘图标\n\n" +
                    "已保存的调校与规则会被保留，随时可以回到调校页重新应用。",
                    "停用全部功能"))
                return;

            FactoryResetReport? report = await RunAsync(DisableAllButton, "停用",
                () => FactoryResetService.DisableAllFeaturesAsync());

            if (report == null)
                return;

            if (report.AllSucceeded)
                ToastService.Info("已停用全部功能");
            else
                ToastService.Warning($"已停用，但有 {report.Failed.Count} 项未完成（详见日志页）");
        }

        /// <summary>清除全部数据：恢复出厂，配置一并抹掉。</summary>
        private async void FactoryResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!DarkDialog.Confirm(
                    "确定要清除全部数据吗？\n\n" +
                    "· 停止后台调度，解除全部进程的绑核\n" +
                    "· 移除开机自启计划任务\n" +
                    "· 把 CO / FMax 与电源方案写回出厂值\n" +
                    "· 清空调校、规则、掩码、方案与日志文件\n" +
                    "· 界面设置恢复默认\n\n" +
                    "此操作不可撤销。",
                    "清除全部数据"))
                return;

            FactoryResetReport? report = await RunAsync(FactoryResetButton, "清除",
                () => FactoryResetService.ResetToFactoryAsync());

            if (report == null)
                return;

            if (report.AllSucceeded)
                ToastService.Info("已恢复出厂设置");
            else
                ToastService.Warning($"已完成，但有 {report.Failed.Count} 项失败（详见日志页）");
        }

        /// <summary>
        /// 执行一个破坏性动作并接管按钮的三态（可用 → 执行中 → 可用）。
        ///
        /// 按钮必须先禁用：这两个动作都要跑好几秒（写 SMU + 删文件都在后台线程），
        /// 期间再点一次会并发执行同一套步骤，第二次大概率撞上正在删的文件。
        /// </summary>
        private async Task<FactoryResetReport?> RunAsync(
            Button button, string verb, Func<Task<FactoryResetReport>> action)
        {
            button.IsEnabled = false;
            button.Content = verb + "中…";

            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                LogService.Critical($"高级页操作异常：{ex}");
                ToastService.Error($"操作过程出现异常：{ex.Message}（请查看日志确认已完成的部分）");
                return null;
            }
            finally
            {
                button.Content = verb;
                button.IsEnabled = true;
            }
        }
    }
}
