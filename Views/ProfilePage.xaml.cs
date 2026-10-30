using System.Windows.Controls;

namespace ZenForge.Views
{
    /// <summary>
    /// 配置页：多套「调校 + 调度」方案的保存与切换。
    ///
    /// 【当前状态：骨架已定，功能留到 v1.1.x】
    /// 本页现在没有任何事件处理逻辑，卡片里的按钮与开关都是禁用态 ——
    /// 这是刻意的：不画假数据、不摆能点但没反应的控件。
    ///
    /// 为什么先交付骨架：
    /// 方案模型（方案 = 一份 CO/FMax 快照 + 一组绑核规则 + 一组优先级）
    /// 牵连三个 store，profiles.json 的字段还没定；场景自动切换要挂在
    /// ProcessWatcher 上，而那个类现在只处理「绑核」，要扩成「监听 + 触发动作」。
    /// 这两处都属于结构性改动，放在 v1.1.x 一并做。
    /// 那时只需接上数据源与 IsEnabled，本页版式一行不用改。
    /// </summary>
    public partial class ProfilePage : UserControl
    {
        public const string PageKey = "config";

        public ProfilePage() => InitializeComponent();
    }
}
