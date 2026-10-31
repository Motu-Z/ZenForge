namespace Zentwik.Core.Services
{
    public enum LogLevel
    {
        /// <summary>详细运行信息，只在诊断版或显式开启时才落盘。</summary>
        Debug,

        Info,

        Warn,

        Error,

        /// <summary>
        /// 致命错误：进程已无法继续正常工作。
        ///
        /// 与 <see cref="Error"/> 的区别不是严重程度，而是要不要用户动手：
        /// Error 是「这次操作失败了，重试或换参数」；
        /// Critical 是「程序坏了，请把日志发我」。
        /// </summary>
        Critical
    }
}
