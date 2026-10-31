using System.Windows;
using System.Windows.Controls;
using Zentwik.Services;

namespace Zentwik.Views
{
    /// <summary>
    /// 配置页：多套「调校 + 调度」方案的保存与切换，以及场景自动切换的总闸。
    ///
    /// 【UI 层只做事件路由，决策全在 Service 层】
    /// 本页不判断「该切哪套方案」「这次保存会不会覆盖谁」——
    /// 那些是 ProfileService / ScenarioService 的事。这里只做三件事：
    /// 把集合挂到列表上、把点击翻译成一次服务调用、把结果提示出去。
    ///
    /// 【生命周期】
    /// 订阅与退订走 IPageLifecycle，不用 Loaded / Unloaded：
    /// 路由复用页面实例，Loaded 只在第一次挂进视觉树时触发一次，
    /// 第二次切回来不会再触发（Loaded / Unloaded 的这个坑见 NavigationRouter 注释）。
    /// </summary>
    public partial class ProfilePage : UserControl, IPageLifecycle
    {
        public const string PageKey = "config";

        public ProfilePage()
        {
            InitializeComponent();

            // 直接绑服务里的集合：它俩都是 ObservableCollection，
            // 服务内部一律增量更新，界面不需要自己同步第二份列表。
            ProfileList.ItemsSource = Svc.Profiles.Items;
            ScenarioList.ItemsSource = Svc.Scenarios.Rules;
        }

        public void OnActivated()
        {
            Svc.Profiles.Changed += OnServiceChanged;
            Svc.Scenarios.Changed += OnServiceChanged;

            // 每次进页面重读一次：方案文件可能被后台的静默实例（-silent）改过，
            // 内存里那份不是唯一真相。
            Svc.Profiles.Load();
            Svc.Scenarios.Load();

            Refresh();
        }

        public void OnDeactivated()
        {
            // 必须退订：Changed 是静态事件，页面实例会被路由长期持有，
            // 不退订就是「每次进页面多一个回调」，切十次就是十个。
            Svc.Profiles.Changed -= OnServiceChanged;
            Svc.Scenarios.Changed -= OnServiceChanged;
        }

        private void OnServiceChanged() => Refresh();

        /// <summary>刷新开关状态与两条空状态行。</summary>
        private void Refresh()
        {
            // 先摘掉事件再赋值：开关的初值同步不该被当成一次用户操作写回服务。
            ScenarioToggle.Checked -= ScenarioToggle_Checked;
            ScenarioToggle.Unchecked -= ScenarioToggle_Unchecked;
            ScenarioToggle.IsChecked = Svc.Scenarios.Enabled;
            ScenarioToggle.Checked += ScenarioToggle_Checked;
            ScenarioToggle.Unchecked += ScenarioToggle_Unchecked;

            ScenarioEmpty.Visibility = Svc.Scenarios.Rules.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            ProfileEmpty.Visibility = Svc.Profiles.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// 保存当前设置为一份新方案。
        ///
        /// 名字自动取「方案 N」而不弹输入框：这一页的定位是「先把当前状态存下来」，
        /// 命名是事后整理的事。为一个自动动作塞一个输入框，是把简单操作复杂化。
        /// </summary>
        private void SaveCurrent_Click(object sender, RoutedEventArgs e)
        {
            int index = Svc.Profiles.Items.Count + 1;

            // 重名就往后找空位：删掉中间某一份之后，「数量 + 1」会撞上已有的名字
            while (FindByName("方案 " + index.ToString()) != null)
                index++;

            string name = "方案 " + index.ToString();
            Svc.Profiles.Save(Svc.Profiles.CaptureCurrent(name));
            ToastService.Success($"已保存「{name}」");
        }

        private void ApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string id })
                return;

            if (Svc.Profiles.Apply(id))
                ToastService.Success("已切换方案");
            else
                ToastService.Error("方案不存在");
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string id })
                return;

            string name = Svc.Profiles.FindById(id)?.Name ?? id;

            // 删除没有回收站，必须问一句
            if (!DarkDialog.Confirm($"删除方案「{name}」？", "Zentwik"))
                return;

            Svc.Profiles.Delete(id);
        }

        private void ScenarioToggle_Checked(object sender, RoutedEventArgs e)
            => Svc.Scenarios.SetEnabled(true);

        private void ScenarioToggle_Unchecked(object sender, RoutedEventArgs e)
            => Svc.Scenarios.SetEnabled(false);

        /// <summary>按名字找方案（保存时避开重名）。</summary>
        private static ProfileSnapshot? FindByName(string name)
        {
            foreach (ProfileSnapshot snapshot in Svc.Profiles.Items)
                if (snapshot.Name == name)
                    return snapshot;
            return null;
        }
    }
}
