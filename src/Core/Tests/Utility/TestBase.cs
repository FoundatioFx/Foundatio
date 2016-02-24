using System;
using System.Collections.Generic;
using Foundatio.Logging;
using Foundatio.Utility;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public abstract class TestBase {
        protected readonly ILogger _logger;

        protected TestBase(ITestOutputHelper output) {
            LoggerFactory = new TestLoggerFactory(output);
            _logger = LoggerFactory.CreateLogger(GetType());
        }

        protected TestLoggerFactory LoggerFactory { get; }
    }

    public class TestLoggerFactory : ILoggerFactory {
        private readonly Dictionary<string, LogLevel> _logLevels = new Dictionary<string, LogLevel>();
        private readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private readonly ITestOutputHelper _testOutputHelper;

        public TestLoggerFactory(ITestOutputHelper output) {
            _testOutputHelper = output;
        }

        public LogLevel MinimumLevel { get; set; }
        public IReadOnlyList<LogEntry> LogEntries => _logEntries;
        public int MaxLogEntries = 1000;

        public void AddLogEntry(LogEntry logEntry) {
            if (_logEntries.Count >= MaxLogEntries)
                return;

            lock (_logEntries)
                _logEntries.Add(logEntry);

            if (!ShouldWriteToTestOutput)
                return;

            try {
                _testOutputHelper.WriteLine(logEntry.ToString());
            } catch (Exception) { }
        }

        public ILogger CreateLogger(string categoryName) {
            return new TestLogger(categoryName, this);
        }

        public bool ShouldWriteToTestOutput { get; set; } = true;

        public bool IsEnabled(string category, LogLevel logLevel) {
            LogLevel categoryLevel;
            if (_logLevels.TryGetValue(category, out categoryLevel))
                return logLevel >= categoryLevel;

            return logLevel >= MinimumLevel;
        }

        public void SetLogLevel(string category, LogLevel minLogLevel) {
            _logLevels[category] = minLogLevel;
        }

        public void Dispose() {}
    }

    public class TestLogger : ILogger {
        private readonly TestLoggerFactory _loggerFactory;
        private readonly string _categoryName;
        private readonly Stack<object> _scope = new Stack<object>();

        public TestLogger(string categoryName, TestLoggerFactory loggerFactory) {
            _loggerFactory = loggerFactory;
            _categoryName = categoryName;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            if (!_loggerFactory.IsEnabled(_categoryName, logLevel))
                return;

            var logEntry = new LogEntry {
                Date = DateTime.UtcNow,
                LogLevel = logLevel,
                EventId = eventId,
                State = state,
                Exception = exception,
                Formatter = (s, ex) => formatter(state, exception),
                CategoryName = _categoryName,
                Scope = _scope.ToArray()
            };

            _loggerFactory.AddLogEntry(logEntry);
        }

        public bool IsEnabled(LogLevel logLevel) {
            return logLevel >= _loggerFactory.MinimumLevel;
        }

        public IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state) {
            _scope.Push(scopeFactory(state));
            return new DisposableAction(() => _scope.Pop());
        }
    }

    public class LogEntry {
        public DateTime Date { get; set; }
        public string CategoryName { get; set; }
        public LogLevel LogLevel { get; set; }
        public object[] Scope { get; set; }
        public EventId EventId { get; set; }
        public object State { get; set; }
        public Exception Exception { get; set; }

        public Func<object, Exception, string> Formatter { get; set; }

        public string GetMessage() {
            return Formatter(State, Exception);
        }

        public override string ToString() {
            return String.Concat("", Date.ToString("HH:mm:ss.fff"), " ", LogLevel.ToString().Substring(0, 1).ToUpper(), ":", CategoryName, " - ", GetMessage());
        }
    }
}