using System;

namespace Zentwik.Core.Services
{
    public sealed class LogEntry
    {
        public DateTime Time { get; init; }

        public LogLevel Level { get; init; }

        public string Message { get; init; } = string.Empty;

        public string LevelText => Level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "INFO"
        };
    }
}
