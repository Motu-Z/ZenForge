using System;

namespace Zentwik.Core.Threading
{
    /// <summary>
    /// 可延后挂载的 <see cref="IUiDispatcher"/>。
    ///
    /// 进程入口（Main）必须先于界面创建容器：异常兜底、日志、路径在 UI 起来之前就要能用。
    /// 但 UI 线程的同步上下文只有界面框架启动后才有，因此先注入这一层占位，
    /// 等宿主就绪再 <see cref="Attach"/> 真正的调度器。
    /// 未挂载时 <see cref="Post"/> 直接就地执行 —— 无界面进程（-silent）永远走这条分支。
    /// </summary>
    public sealed class DelegatingUiDispatcher : IUiDispatcher
    {
        private IUiDispatcher? _inner;

        public bool IsCurrentThread => _inner?.IsCurrentThread ?? false;

        /// <summary>挂载真实调度器。重复挂载以最后一次为准。</summary>
        public void Attach(IUiDispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            _inner = dispatcher;
        }

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (_inner is { } inner)
                inner.Post(action);
            else
                action();
        }
    }
}
