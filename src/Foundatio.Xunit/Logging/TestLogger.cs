using System;
using System.Collections.Generic;
using System.Threading;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Xunit;

public class TestLogger : ILoggerFactory
{
    private readonly Dictionary<string, LogLevel> _logLevels = new();
    private readonly Queue<LogEntry> _logEntries = new();

    public TestLogger()
    {
        Options = new TestLoggerOptions();
    }

    public TestLogger(TestLoggerOptions options)
    {
        Options = options ?? new TestLoggerOptions();
    }

    public TestLoggerOptions Options { get; }
    public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();

    public void Clear() => _logEntries.Clear();

    internal void AddLogEntry(LogEntry logEntry)
    {
        lock (_logEntries)
        {
            _logEntries.Enqueue(logEntry);

            if (_logEntries.Count > Options.MaxLogEntriesToStore)
                _logEntries.Dequeue();
        }

        if (Options.WriteLogEntryFunc == null || _logEntriesWritten >= Options.MaxLogEntriesToWrite)
            return;

        try
        {
            Options.WriteLogEntry(logEntry);
            Interlocked.Increment(ref _logEntriesWritten);
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private int _logEntriesWritten = 0;

    public ILogger CreateLogger(string categoryName)
    {
        return new TestLoggerLogger(categoryName, this);
    }

    public void AddProvider(ILoggerProvider loggerProvider) { }

    public bool IsEnabled(string category, LogLevel logLevel)
    {
        if (_logLevels.TryGetValue(category, out var categoryLevel))
            return logLevel >= categoryLevel;

        return logLevel >= Options.DefaultMinimumLevel;
    }

    public void SetLogLevel(string category, LogLevel minLogLevel)
    {
        _logLevels[category] = minLogLevel;
    }

    public void SetLogLevel<T>(LogLevel minLogLevel)
    {
        SetLogLevel(TypeHelper.GetTypeDisplayName(typeof(T)), minLogLevel);
    }

    public void Dispose() { }
}
