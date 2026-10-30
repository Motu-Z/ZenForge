using System.Windows;
using System.Windows.Controls;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 配置页：多套「调校 + 调度」方案的保存与切换。
    ///
    /// 【当前状态：只有版式，功能未接线】
    /// 1.0.0-can4 先把界面做出来评审，所有可点元素统一走 <see cref="NotImplemented_Click"/>，
    /// 弹一条 Toast 说明「尚未实装」然后返回。
    ///
    /// 为什么不接着一并写掉：
    ///   · 方案模型（方案 = 一份 CO/FMax 快照 + 一组绑核规则 + 一组优先级）牵连三个 store，
    ///     profiles.json 的字段还没定；
    ///   · 场景自动切换要挂在 ProcessWatcher 上，而那个类现在只处理"绑核"，
    ///     要扩成"监听 + 触发动作"，属于结构性改动；
    ///   · 版式一旦被要求调整（比如卡片顺序换了），已写好的绑定代码要跟着返工。
    ///
    /// 所以这一版先把「看得见的结构」定下来：卡片顺序、双列等高、每行信息密度。
    /// </summary>
    public partial class ProfilePage : Page
    {
        public ProfilePage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 尚未实装的交互统一入口。
        ///
        /// 用 Toast 而不是弹窗：这些按钮不是"操作失败"，只是"还没做"，
        /// 弹窗会让人以为哪里出错了。Toast 一闪而过，说明清楚又不打断。
        /// </summary>
        private void NotImplemented_Click(object sender, RoutedEventArgs e)
        {
            string what = sender switch
            {
                Button { Content: string text } => text.TrimStart('+', ' '),
                _ => "该功能",
            };

            ToastService.Info($"「{what}」尚未实装，本版本仅用于确认界面版式");
            LogService.Info($"配置页：点到了尚未实装的入口「{what}」");
        }
    }
}
