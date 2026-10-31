using System.Runtime.CompilerServices;

namespace Zenwik
{
    /// <summary>
    /// WPF 宿主。全项目只有这里允许在「进程入口」这一层出现 App / Application。
    ///
    /// 两个入口方法都标了 NoInlining：Program.Main 调用它们时，JIT 不能把方法体
    /// 内联进来，否则 WPF 类型会被拖进纯后台路径，轻量前提当场失效。
    /// </summary>
    internal static class GuiHost
    {
        /// <summary>
        /// 正常启动。单实例、提权、主题、主窗口，全部按 App.OnStartup 的原有流程走。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Start()
        {
            App app = new();
            app.InitializeComponent();
            return app.Run();
        }

        /// <summary>
        /// 从后台宿主升级。单实例锁、ProcessWatcher、管理员权限都已被后台宿主持有，
        /// App.OnStartup 会走精简分支：只把界面搭起来，不重复抢锁、不重复写 SMU。
        ///
        /// 这里分三步打日志而不是一句「开始升级」：升级链横跨
        /// HeadlessHost → App 构造 → InitializeComponent → OnStartup → 建窗口，
        /// 出问题时日志停在哪个编号，就知道断在哪一环。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Upgrade()
        {
            App.IsHeadlessUpgrade = true;

            Zenwik.Core.Services.LogService.Info("升级 2/6：创建 Application 实例");

            App app = new();

            Zenwik.Core.Services.LogService.Info("升级 3/6：加载 App.xaml 资源字典");
            app.InitializeComponent();

            return app.Run();
        }
    }
}
