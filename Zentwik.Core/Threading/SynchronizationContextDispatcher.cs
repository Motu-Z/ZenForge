using System;
using System.Threading;

namespace Zentwik.Core.Threading
{
    /// <summary>
    /// 基于 <see cref="SynchronizationContext"/> 的 <see cref="IUiDispatcher"/>。
    ///
    /// WinUI 3 的接入方式是 <c>DispatcherQueue.AsSynchronizationContext()</c>，
    /// WPF 是 <c>DispatcherSynchronizationContext</c>。两者都派生自
    /// SynchronizationContext，因此这一份实现同时服务两代 UI，Core 不需要为它们分别开洞。
    /// </summary>
    public sealed class SynchronizationContextDispatcher : IUiDispatcher
    {
        private readonly SynchronizationContext _context;

        public SynchronizationContextDispatcher(SynchronizationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context;
        }

        public bool IsCurrentThread => ReferenceEquals(SynchronizationContext.Current, _context);

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            // 静态 lambda + state 参数：避免每次调度都捕获一个闭包对象。
            // 日志写入是高频路径，这里每秒可能产生上百次分配。
            _context.Post(static state => ((Action)state!)(), action);
        }
    }
}
