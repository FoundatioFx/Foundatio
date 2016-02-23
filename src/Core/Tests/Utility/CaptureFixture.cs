using System;
using System.Collections.Generic;
using System.IO;
using Foundatio.Logging;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public abstract class CaptureTests {
        protected readonly ILogger _logger;
        protected readonly TextWriter _writer;

        protected CaptureTests(ITestOutputHelper output) {
            LoggerFactory = new TestLoggerFactory(output);
            LoggerFactory.MinimumLevel = LogLevel.Debug;
            _logger = LoggerFactory.CreateLogger(GetType());
            _writer = new TestOutputWriter(output);
        }

        protected ILoggerFactory LoggerFactory { get; }
    }

    public class TestLoggerFactory : ILoggerFactory {
        public TestLoggerFactory(ITestOutputHelper output) {
            TestOutputHelper = output;
        }

        public LogLevel MinimumLevel { get; set; }
        public IList<LogEntry> LogEntries { get; } = new List<LogEntry>(); 
        public ITestOutputHelper TestOutputHelper { get; }

        public ILogger CreateLogger(string categoryName) {
            return new TestLogger(categoryName, this);
        }

        public void AddProvider(ILoggerProvider provider) {}

        public void Dispose() {}
    }

    public class TestLogger : ILogger {
        private readonly TestLoggerFactory _loggerFactory;
        private readonly string _name;
        private readonly Stack<object> _scope = new Stack<object>();

        public TestLogger(string name, TestLoggerFactory loggerFactory) {
            _loggerFactory = loggerFactory;
            _name = name;
        }

        public void Log(LogLevel logLevel, int eventId, object state, Exception exception, Func<object, Exception, string> formatter) {
            if (!IsEnabled(logLevel))
                return;

            var logEntry = new LogEntry {
                LogLevel = logLevel,
                EventId = eventId,
                State = state,
                Exception = exception,
                Formatter = formatter,
                LoggerName = _name,
                Scope = _scope.ToArray()
            };

            try {
                _loggerFactory.TestOutputHelper.WriteLine(logEntry.GetMessage());
            } catch (Exception) { }

            lock (_loggerFactory.LogEntries)
                _loggerFactory.LogEntries.Add(logEntry);
        }

        public bool IsEnabled(LogLevel logLevel) {
            return logLevel >= _loggerFactory.MinimumLevel;
        }

        public IDisposable BeginScopeImpl(object state) {
            _scope.Push(state);
            return new DisposableAction(() => _scope.Pop());
        }
    }

    public class LogEntry {
        public LogLevel LogLevel { get; set; }

        public int EventId { get; set; }

        public object State { get; set; }

        public Exception Exception { get; set; }

        public Func<object, Exception, string> Formatter { get; set; }

        public object[] Scope { get; set; }

        public string LoggerName { get; set; }

        public string GetMessage() {
            return Formatter(State, Exception);
        }

        public override string ToString() {
            return String.Concat("[", LoggerName, ":", LogLevel.ToString(), "] ", GetMessage());
        }
    }
}