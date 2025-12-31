using System;
using System.Collections.Generic;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public class TestLoggerOptions
{
    /// <summary>
    /// The minimum log level for the logging system. Anything below this level will not be processed and will not be written to the output even if a specific category has a lower log level set.
    /// </summary>
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Trace;
    public LogLevel DefaultLogLevel { get; set; } = LogLevel.Information;
    public Dictionary<string, LogLevel> LogLevels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int MaxLogEntriesToStore { get; set; } = 100;
    public int MaxLogEntriesToWrite { get; set; } = 1000;
    public bool IncludeScopes { get; set; } = true;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public void SetLogLevel(string category, LogLevel minLogLevel)
    {
        LogLevels[category] = minLogLevel;
    }

    public void SetLogLevel<T>(LogLevel minLogLevel)
    {
        SetLogLevel(TypeHelper.GetTypeDisplayName(typeof(T)), minLogLevel);
    }

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
