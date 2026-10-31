using System.IO;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 日志服务的构造参数。
    ///
    /// 上限值集中在这里，是为了让「内存天花板」可计算、可调整，而不是散落在写入路径上：
    /// 界面条数 × 单条字符上限（<see cref="MaxEntries"/> × <see cref="MaxMessageChars"/>）
    /// 才是真实的常驻内存上界。
    /// </summary>
    public sealed class LogOptions
    {
        /// <summary>日志目录。默认 <c>%AppData%\Zentwik\Logs</c>。</summary>
        public string Directory { get; init; } = string.Empty;

        /// <summary>
        /// 界面日志条数上限。超出后按 <see cref="TrimBatch"/> 成批丢弃头部条目。
        /// 程序常驻，集合只增不减会把内存吃光；翻历史是磁盘日志的职责。
        /// </summary>
        public int MaxEntries { get; init; } = 200;

        /// <summary>单次丢弃的批量。逐条删会让列表每条日志都整体重排一次。</summary>
        public int TrimBatch { get; init; } = 32;

        /// <summary>
        /// 单条消息进入内存集合时的长度上限。
        /// 条数上限管不了「一条多大」：一条未捕获异常堆栈就有几 KB。
        /// 只截内存不截落盘，磁盘是事后排查的唯一凭据。
        /// </summary>
        public int MaxMessageChars { get; init; } = 2000;

        /// <summary>日志文件留存天数，超期的在启动时删除。</summary>
        public int RetentionDays { get; init; } = 14;

        /// <summary>待写队列容量上限，防止磁盘卡顿时积压成隐形内存泄漏。</summary>
        public int MaxPendingEntries { get; init; } = 800;

        /// <summary>初始是否记录 Debug 级。</summary>
        public bool Verbose { get; init; }

        internal LogOptions Normalize()
        {
            return new LogOptions
            {
                Directory = string.IsNullOrWhiteSpace(Directory) ? Path.GetTempPath() : Directory,
                MaxEntries = MaxEntries > 0 ? MaxEntries : 200,
                TrimBatch = TrimBatch > 0 ? TrimBatch : 32,
                MaxMessageChars = MaxMessageChars > 0 ? MaxMessageChars : 2000,
                RetentionDays = RetentionDays > 0 ? RetentionDays : 14,
                MaxPendingEntries = MaxPendingEntries > 0 ? MaxPendingEntries : 800,
                Verbose = Verbose
            };
        }
    }
}
