using System;
using System.Collections.Generic;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Tests.Utility {
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
}