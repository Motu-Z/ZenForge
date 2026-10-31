using System;

namespace Zentwik.Core.Threading
{
    /// <summary>
    /// UI 线程调度抽象。
    ///
    /// Core 不认识 DispatcherQueue 也不认识 WPF Dispatcher：前者是 WinUI 3 独有，
    /// 后者是 WindowsBase 独有，Core 引用任何一个都会破坏「零 UI 框架依赖」的红线。
    /// 宿主在启动时把自己的同步上下文包装成本接口注入进来。
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>当前线程是否就是 UI 线程。</summary>
        bool IsCurrentThread { get; }

        /// <summary>把回调排到 UI 线程。调用方不等待其执行。</summary>
        void Post(Action action);
    }
}
