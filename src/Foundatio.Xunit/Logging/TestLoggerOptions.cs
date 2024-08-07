using System;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public class TestLoggerOptions
{
    public LogLevel DefaultMinimumLevel { get; set; } = LogLevel.Information;
    public int MaxLogEntriesToStore { get; set; } = 100;
    public int MaxLogEntriesToWrite { get; set; } = 1000;
    public bool IncludeScopes { get; set; } = true;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public void UseOutputHelper(Func<ITestOutputHelper> getOutputHelper, Func<LogEntry, string> formatLogEntry = null)
    {
        formatLogEntry ??= logEntry => logEntry.ToString(false);
        WriteLogEntryFunc = logEntry =>
        {
            getOutputHelper?.Invoke()?.WriteLine(formatLogEntry(logEntry));
        };
    }

    public Action<LogEntry> WriteLogEntryFunc { get; set; }
    internal void WriteLogEntry(LogEntry logEntry) => WriteLogEntryFunc?.Invoke(logEntry);

    public Func<DateTimeOffset> NowFunc { get; set; }
    internal DateTimeOffset GetNow() => NowFunc?.Invoke() ?? TimeProvider.GetUtcNow();
}
