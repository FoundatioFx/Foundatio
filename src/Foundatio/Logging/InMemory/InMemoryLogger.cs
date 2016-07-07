using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Utility;

namespace Foundatio.Logging.InMemory {
    public class InMemoryLogger : ILogger {
        private readonly string _categoryName;
        private readonly InMemoryLoggerFactory _loggerFactory;
        private readonly ConcurrentStack<object> _stack = new ConcurrentStack<object>();

        public InMemoryLogger(string categoryName, InMemoryLoggerFactory loggerFactory) {
            _categoryName = categoryName;
            _loggerFactory = loggerFactory;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            if (!IsEnabled(logLevel))
                return;

            var scopes = _stack.Reverse().ToArray();
            var logEntry = new LogEntry {
                Date = SystemClock.UtcNow,
                LogLevel = logLevel,
                EventId = eventId,
                State = state,
                Exception = exception,
                Message = formatter(state, exception),
                CategoryName = _categoryName,
                Scopes = scopes
            };

            var logData = state as LogData;
            if (logData != null) {
                logEntry.Properties["CallerMemberName"] = logData.MemberName;
                logEntry.Properties["CallerFilePath"] = logData.FilePath;
                logEntry.Properties["CallerLineNumber"] = logData.LineNumber;

                foreach (var property in logData.Properties)
                    logEntry.Properties[property.Key] = property.Value;
            } else {
                var logDictionary = state as IDictionary<string, object>;
                if (logDictionary != null) {
                    foreach (var property in logDictionary)
                        logEntry.Properties[property.Key] = property.Value;
                }
            }

            foreach (var scope in scopes) {
                var scopeData = scope as IDictionary<string, object>;
                if (scopeData == null)
                    continue;

                foreach (var property in scopeData)
                    logEntry.Properties[property.Key] = property.Value;
            }

            _loggerFactory.AddLogEntry(logEntry);
        }

        public bool IsEnabled(LogLevel logLevel) {
            return _loggerFactory.IsEnabled(_categoryName, logLevel);
        }

        public IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state) {
            _stack.Push(scopeFactory(state));
            return new DisposableAction(() => {
                object s;
                _stack.TryPop(out s);
            });
        }
    }
}
