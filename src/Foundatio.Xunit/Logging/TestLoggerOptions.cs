using System;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Xunit;

public class TestLoggerOptions
{
    public LogLevel DefaultMinimumLevel { get; set; } = LogLevel.Information;
    public int MaxLogEntriesToStore { get; set; } = 100;
    public int MaxLogEntriesToWrite { get; set; } = 1000;
    public bool IncludeScopes { get; set; } = true;

    public Action<LogEntry> WriteLogEntryFunc { get; set; }
    internal void WriteLogEntry(LogEntry logEntry) => WriteLogEntryFunc?.Invoke(logEntry);

    public Func<DateTimeOffset> NowFunc { get; set; }
    internal DateTimeOffset GetNow() => NowFunc?.Invoke() ?? SystemClock.OffsetNow;
}
