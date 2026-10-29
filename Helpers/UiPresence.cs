namespace ZenForge.Helpers
{
    /// <summary>
    /// 主窗口是否处于「用户看得见」的状态。
    ///
    /// 存在的理由：后台组件（如 ProcessWatcher）需要判断「这会儿弹提示会不会变成
    /// 一个没人能关的孤儿窗口」，但它们绝不能为此引用 App —— App 的基类是 WPF 的
    /// Application，一引用就把 PresentationFramework 拖进纯后台进程，
    /// 轻量常驻的整个前提就没了。
    ///
    /// 这个类只有 bool，零依赖，谁都能读。
    /// </summary>
    public static class UiPresence
    {
        /// <summary>主窗口可见且未最小化。由 MainWindow 在显示 / 隐藏 / 最小化时更新。</summary>
        public static volatile bool MainWindowVisible;
    }
}
