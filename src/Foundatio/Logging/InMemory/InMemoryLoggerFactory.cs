using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Logging.InMemory {
    public class InMemoryLoggerFactory : ILoggerFactory {
        private readonly Dictionary<string, LogLevel> _logLevels = new Dictionary<string, LogLevel>();
        private readonly Queue<LogEntry> _logEntries = new Queue<LogEntry>();

        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
        public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();
        public int MaxLogEntries = 1000;

        public List<LogEntry> GetLogEntries(int entryCount = 10) {
            return new List<LogEntry>(_logEntries.OrderByDescending(l => l.Date).Take(entryCount).ToArray());
        }

        internal void AddLogEntry(LogEntry logEntry) {
            if (MaxLogEntries <= 0 || logEntry == null)
                return;

            lock (_logEntries) {
                _logEntries.Enqueue(logEntry);

                while (_logEntries.Count > Math.Max(0, MaxLogEntries))
                    _logEntries.Dequeue();
            }
        }

        public ILogger CreateLogger(string categoryName) {
            return new InMemoryLogger(categoryName, this);
        }

        public void AddProvider(ILoggerProvider loggerProvider) { }

        public bool IsEnabled(string category, LogLevel logLevel) {
            if (MaxLogEntries <= 0)
                return false;

            LogLevel categoryLevel;
            if (_logLevels.TryGetValue(category, out categoryLevel))
                return logLevel >= categoryLevel;

            return logLevel >= MinimumLevel;
        }

        public void SetLogLevel(string category, LogLevel minLogLevel) {
            _logLevels[category] = minLogLevel;
        }
    }
}