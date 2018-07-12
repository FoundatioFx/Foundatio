using System;
using System.Collections.Generic;
using System.Threading;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Logging.Xunit {
    public class TestLoggerFactory : ILoggerFactory {
        private readonly Dictionary<string, LogLevel> _logLevels = new Dictionary<string, LogLevel>();
        private readonly Queue<LogEntry> _logEntries = new Queue<LogEntry>();
        private readonly Action<LogEntry> _writeLogEntryFunc;

        public TestLoggerFactory(Action<LogEntry> writeLogEntryFunc) {
            _writeLogEntryFunc = writeLogEntryFunc;
        }

        public TestLoggerFactory(ITestOutputHelper output) : this(e => output.WriteLine(e.ToString(false))) {}

        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
        public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();
        public int MaxLogEntriesToStore = 1000;
        public int MaxLogEntriesToWrite = 1000;

        internal void AddLogEntry(LogEntry logEntry) {
            lock (_logEntries) {
                _logEntries.Enqueue(logEntry);

                if (_logEntries.Count > MaxLogEntriesToStore)
                    _logEntries.Dequeue();
            }
            
            if (!ShouldWriteToTestOutput || _logEntriesWritten >= MaxLogEntriesToWrite)
                return;

            try {
                _writeLogEntryFunc(logEntry);
                Interlocked.Increment(ref _logEntriesWritten);
            } catch (Exception) { }
        }

        private int _logEntriesWritten = 0;

        public ILogger CreateLogger(string categoryName) {
            return new TestLogger(categoryName, this);
        }

        public void AddProvider(ILoggerProvider loggerProvider) {}

        public bool ShouldWriteToTestOutput { get; set; } = true;

        public bool IsEnabled(string category, LogLevel logLevel) {
            if (_logLevels.TryGetValue(category, out LogLevel categoryLevel))
                return logLevel >= categoryLevel;

            return logLevel >= MinimumLevel;
        }

        public void SetLogLevel(string category, LogLevel minLogLevel) {
            _logLevels[category] = minLogLevel;
        }

        public void SetLogLevel<T>(LogLevel minLogLevel) {
            SetLogLevel(TypeHelper.GetTypeDisplayName(typeof(T)), minLogLevel);
        }

        public void Dispose() {}
    }
}