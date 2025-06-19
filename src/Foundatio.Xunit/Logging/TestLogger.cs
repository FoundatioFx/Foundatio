using System;
using System.Collections.Generic;
using System.Threading;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public class TestLogger : ILoggerFactory
{
    private readonly Queue<LogEntry> _logEntries = new();
    private int _logEntriesWritten;

    public TestLogger(Action<TestLoggerOptions> configure = null)
    {
        Options = new TestLoggerOptions();
        configure?.Invoke(Options);

        foreach (var logLevel in Options.LogLevels)
            SetLogLevel(logLevel.Key, logLevel.Value);
    }

    public TestLogger(ITestOutputHelper output, Action<TestLoggerOptions> configure = null)
    {
        Options = new TestLoggerOptions
        {
            WriteLogEntryFunc = logEntry =>
            {
                output.WriteLine(logEntry.ToString(false));
            }
        };

        configure?.Invoke(Options);

        foreach (var logLevel in Options.LogLevels)
            SetLogLevel(logLevel.Key, logLevel.Value);
    }

    public TestLogger(TestLoggerOptions options)
    {
        Options = options ?? new TestLoggerOptions();

        foreach (var logLevel in Options.LogLevels)
            SetLogLevel(logLevel.Key, logLevel.Value);

    }

    public TestLoggerOptions Options { get; }

    public LogLevel DefaultLogLevel
    {
        get => Options.DefaultLogLevel;
        set => Options.DefaultLogLevel = value;
    }

    public int MaxLogEntriesToStore
    {
        get => Options.MaxLogEntriesToStore;
        set => Options.MaxLogEntriesToStore = value;
    }

    public int MaxLogEntriesToWrite
    {
        get => Options.MaxLogEntriesToWrite;
        set => Options.MaxLogEntriesToWrite = value;
    }

    public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();

    public void Reset()
    {
        lock (_logEntries)
        {
            _logEntries.Clear();

            _lock.EnterWriteLock();
            try
            {
                _root.Children.Clear();
                _root.Level = null;
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            foreach (var logLevel in Options.LogLevels)
                SetLogLevel(logLevel.Key, logLevel.Value);

            Interlocked.Exchange(ref _logEntriesWritten, 0);
        }
    }

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

    public ILogger CreateLogger(string categoryName)
    {
        return new TestLoggerLogger(categoryName, this);
    }

    public void AddProvider(ILoggerProvider loggerProvider) { }

    public bool IsEnabled(string category, LogLevel logLevel)
    {
        ReadOnlySpan<char> span = category.AsSpan();
        Span<(int Start, int Length)> segments = stackalloc (int, int)[20];

        int count = 0;
        int start = 0;

        for (int i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '.')
                continue;

            segments[count++] = (start, i - start);
            start = i + 1;

            if (count == segments.Length)
                break;
        }

        _lock.EnterReadLock();
        try {
            var current = _root;
            LogLevel effectiveLevel = DefaultLogLevel;

            for (int i = 0; i < count; i++) {
                var segment = span.Slice(segments[i].Start, segments[i].Length);
                bool found = false;

                foreach (var kvp in current.Children)
                {
                    if (!segment.Equals(kvp.Key.AsSpan(), StringComparison.Ordinal))
                        continue;

                    current = kvp.Value;
                    found = true;

                    if (current.Level.HasValue)
                        effectiveLevel = current.Level.Value;

                    break;
                }

                if (!found)
                    break;
            }

            return logLevel >= effectiveLevel;
        } finally {
            _lock.ExitReadLock();
        }
    }

    public void SetLogLevel(string category, LogLevel minLogLevel)
    {
        string[] parts = category.Split('.');
        _lock.EnterWriteLock();
        try {
            var current = _root;
            foreach (string part in parts) {
                if (!current.Children.TryGetValue(part, out var child)) {
                    child = new Node();
                    current.Children[part] = child;
                }
                current = child;
            }
            current.Level = minLogLevel;
        } finally {
            _lock.ExitWriteLock();
        }
    }

    public void SetLogLevel<T>(LogLevel minLogLevel)
    {
        SetLogLevel(TypeHelper.GetTypeDisplayName(typeof(T)), minLogLevel);
    }

    public void Dispose() { }

    private class Node {
        public readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public LogLevel? Level;
    }

    private readonly Node _root = new();
    private readonly ReaderWriterLockSlim _lock = new();
}
