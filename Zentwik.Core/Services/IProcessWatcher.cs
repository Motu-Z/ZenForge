using System;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 进程绑核监听。负责按 <c>bindings.json</c> 的规则给新出现的进程自动绑核。
    ///
    /// 实现方保证 <see cref="Start"/> / <see cref="Stop"/> 可重复调用：
    /// 已启动时再次 Start 不产生第二个监听循环。
    /// </summary>
    public interface IProcessWatcher
    {
        /// <summary>当前是否走事件驱动路径。为 false 表示降级为轮询。</summary>
        bool UsingEvents { get; }

        /// <summary>本实例是否持有全局互斥体（即是否为真正执行绑核的那一个）。</summary>
        bool HasOwnership { get; }

        /// <summary>监听循环是否正在运行。</summary>
        bool IsRunning { get; }

        /// <summary>需要告知用户「事件不可用，已降级」时被调用。</summary>
        Action? WmiUnavailableNotifier { get; set; }

        /// <summary>被监听的进程集合发生变化时触发。</summary>
        event Action? ProcessSetChanged;

        void Start();

        void Stop();

        /// <summary>立刻按当前规则重新绑定一遍已在监听的进程。</summary>
        void ReapplyNow();

        /// <summary>清空已处理进程的缓存，让下一轮重新判定。</summary>
        void ResetCache();

        /// <summary>宿主窗口可见性变化，用于调整采样频率。</summary>
        void SetHostVisible(bool visible);
    }
}
