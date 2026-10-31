using System;
using System.Collections.ObjectModel;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 日志服务：内存集合（供界面绑定）+ 按天滚动的日志文件。
    ///
    /// 实现方保证所有写操作最终在 UI 线程落到 <see cref="Entries"/>，
    /// 调用方可以在任意线程直接调用写方法。
    /// </summary>
    public interface ILogService
    {
        /// <summary>界面绑定的日志集合。只读使用，不要从外部增删。</summary>
        ReadOnlyObservableCollection<LogEntry> Entries { get; }

        /// <summary>新增一条日志后在 UI 线程触发。</summary>
        event Action<LogEntry>? EntryAdded;

        /// <summary>是否记录 <see cref="LogLevel.Debug"/> 级详细日志。</summary>
        bool Verbose { get; set; }

        /// <summary>
        /// 暂停记录。暂停期间日志真的丢失，不进内存也不落盘。
        /// <see cref="LogLevel.Critical"/> 不受影响。
        /// </summary>
        bool Paused { get; set; }

        /// <summary>内存集合当前条数。</summary>
        int Count { get; }

        long CountInfo { get; }

        long CountWarn { get; }

        long CountError { get; }

        long CountCritical { get; }

        string LogDirectory { get; }

        /// <summary>详细日志，仅在 <see cref="Verbose"/> 为真时写入。</summary>
        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);

        /// <summary>致命错误。写入后立刻冲刷磁盘 —— 走到这里时进程可能下一行就没了。</summary>
        void Critical(string message);

        /// <summary>清空内存集合、待写队列与磁盘文件。</summary>
        void Clear();

        /// <summary>把待写队列立刻刷到磁盘。退出前调用，避免丢最后几条。</summary>
        void Flush();

        /// <summary>
        /// 取当前日志的副本。导出与自检必须走这个方法：
        /// 直接遍历 <see cref="Entries"/> 会在后台写入时抛「集合已被修改」。
        /// </summary>
        LogEntry[] Snapshot();
    }
}
