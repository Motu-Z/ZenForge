using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ZenForge.Services;

namespace ZenForge.Views
{
    /// <summary>
    /// 页面在「切入 / 切出」时需要做的事。
    ///
    /// 【基石约定】
    /// 这是页面生命周期的唯一钩子。不允许页面再用 Loaded / Unloaded 自己
    /// 挂载定时器与订阅 —— 那两个事件在路由复用页面实例时语义会错：
    /// Loaded 只在第一次挂进视觉树时触发一次，之后切回来不再触发，
    /// 于是「第一次正常、第二次打开就是空白/不刷新」。
    /// </summary>
    public interface IPageLifecycle
    {
        /// <summary>页面成为当前页。挂定时器、订阅服务、必要时拉一次数据。</summary>
        void OnActivated();

        /// <summary>页面被切走。停定时器、退订服务，别在后台空转。</summary>
        void OnDeactivated();
    }

    /// <summary>
    /// 页面路由。整个程序只有这一个地方能切换页面。
    ///
    /// 【为什么不用 Frame】
    /// Frame 自带导航日志（journal），每个条目强引用一个已经卸载的页面；
    /// 页面里有几百行模板时，来回切几次就是好几份旧视觉树留在内存里。
    /// 它还有 NavigationService、前进后退栈、页面缓存模式这一整套我们
    /// 完全用不到的语义 —— 每一样都能成为新的 bug 来源。
    ///
    /// 【页面必须是 UserControl，不能是 Page】
    /// Page 是「被 Window 或 Frame 宿主」的导航单元：它重写了
    /// OnVisualParentChanged，一旦发现视觉父级不是 Window / Frame 就直接抛
    /// InvalidOperationException（"Page 只能具有 Window 或 Frame 父级"）。
    /// 也就是说 ContentControl 根本装不下 Page —— 1.0.2 第一版就是这么崩的：
    /// 每次布局重试都抛一次，一秒上千条堆栈，日志几秒涨到几百兆，界面直接卡死。
    /// 这里约定：路由里的页面一律是 UserControl，不实现导航语义。
    ///
    /// 页面按需创建、创建后复用，切换时调用 OnDeactivated / OnActivated，
    /// 定时器由 <see cref="PageTimerManager"/> 统一挂起与恢复。
    /// </summary>
    public sealed class NavigationRouter
    {
        private readonly ContentControl _host;
        private readonly Dictionary<string, Func<FrameworkElement>> _factories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FrameworkElement> _instances = new(StringComparer.Ordinal);

        private string? _currentKey;

        public NavigationRouter(ContentControl host)
            => _host = host ?? throw new ArgumentNullException(nameof(host));

        /// <summary>当前页面的 key。</summary>
        public string? CurrentKey => _currentKey;

        /// <summary>页面切换完成后触发（参数为新页面 key）。</summary>
        public event EventHandler<string>? Navigated;

        /// <summary>
        /// 登记一个页面。工厂只在第一次导航到该页时执行一次。
        /// key 用短名（"tuning" / "process" …），不用文件路径 ——
        /// 路径是 XAML 的实现细节，改文件名不该影响导航。
        /// </summary>
        public void Register(string key, Func<FrameworkElement> factory)
            => _factories[key] = factory;

        /// <summary>是否已登记该页面。</summary>
        public bool IsRegistered(string key) => _factories.ContainsKey(key);

        /// <summary>
        /// 切到指定页面。重复切到当前页是空操作（不会重跑 OnActivated，
        /// 也不会重建页面）。
        /// </summary>
        public void Navigate(string key)
        {
            if (key == _currentKey)
                return;

            if (!_factories.TryGetValue(key, out Func<FrameworkElement>? factory))
            {
                LogService.Warn($"导航失败：未登记的页面 key「{key}」");
                return;
            }

            // 1. 让旧页面收手（停定时器、退订）。必须在换 Content 之前，
            //    否则旧页面会在切换的一瞬间再跑一次刷新。
            if (_currentKey != null &&
                _instances.TryGetValue(_currentKey, out FrameworkElement? oldPage) &&
                oldPage is IPageLifecycle oldLifecycle)
            {
                oldLifecycle.OnDeactivated();
            }

            // 2. 取页面实例（首次创建）。
            if (!_instances.TryGetValue(key, out FrameworkElement? page))
            {
                page = factory();
                _instances[key] = page;
            }

            // 3. 换内容。先清再设：ContentControl 残留旧 Content 会让
            //    旧页面在视觉树里多挂一帧，定时器恢复时可能拿到过期的可视树。
            _host.Content = null;
            _host.Content = page;
            _currentKey = key;

            // 4. 定时器作用域切到新页面，再让页面自己激活。
            //    顺序不能反：页面 OnActivated 里 Start 的定时器，
            //    需要 PageTimerManager 已经知道「当前页是谁」才会真的跑。
            PageTimerManager.SetActivePage(key);

            if (page is IPageLifecycle lifecycle)
                lifecycle.OnActivated();

            Navigated?.Invoke(this, key);
        }

        /// <summary>
        /// 销毁全部页面实例（退出、全局重置时用）。
        /// 页面若实现了 <see cref="IDisposable"/> 会一并释放。
        /// </summary>
        public void Clear()
        {
            if (_currentKey != null &&
                _instances.TryGetValue(_currentKey, out FrameworkElement? current) &&
                current is IPageLifecycle lifecycle)
            {
                lifecycle.OnDeactivated();
            }

            foreach (FrameworkElement page in _instances.Values)
            {
                if (page is IDisposable disposable)
                    disposable.Dispose();
            }

            _instances.Clear();
            _host.Content = null;
            _currentKey = null;
            PageTimerManager.SetActivePage(null);
        }
    }
}
