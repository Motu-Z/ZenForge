using System.Windows.Controls;

namespace ZenForge.Views
{
    /// <summary>
    /// 配置页：多套「调校 + 调度」方案的保存与切换。
    ///
    /// 【当前状态：功能未实现，页面只给空状态】
    /// 1.0.0-can7 起把整页的演示内容删掉了 —— 上一版画了三张卡并填满
    /// 「cs2.exe → 方案 B」「CO -20 · FMax 4.5G」这类写死的示例，
    /// 看起来像真的在生效，用户会去找「为什么改了没反应」。
    /// 现在只有一句现状说明，没有可点元素，也没有假数据。
    ///
    /// 为什么功能还没做：
    ///   · 方案模型（方案 = 一份 CO/FMax 快照 + 一组绑核规则 + 一组优先级）牵连三个 store，
    ///     profiles.json 的字段还没定；
    ///   · 场景自动切换要挂在 ProcessWatcher 上，而那个类现在只处理「绑核」，
    ///     要扩成「监听 + 触发动作」，属于结构性改动。
    ///
    /// 所以本页暂时没有事件处理逻辑，等模型落地再填。
    /// </summary>
    public partial class ProfilePage : Page
    {
        public ProfilePage()
        {
            InitializeComponent();
        }
    }
}
