using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Foundatio.Utility;

namespace Foundatio.Logging.Xunit {
    internal class TestLogger : ILogger {
        private readonly TestLoggerFactory _loggerFactory;
        private readonly string _categoryName;

        public TestLogger(string categoryName, TestLoggerFactory loggerFactory) {
            _loggerFactory = loggerFactory;
            _categoryName = categoryName;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            if (!_loggerFactory.IsEnabled(_categoryName, logLevel))
                return;

            var scopes = CurrentScopeStack.Reverse().ToArray();
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
            return logLevel >= _loggerFactory.MinimumLevel;
        }

        public IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state) {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            return Push(scopeFactory(state));
        }
        
        private static readonly AsyncLocal<Wrapper> _currentScopeStack = new AsyncLocal<Wrapper>();

        private sealed class Wrapper {
            public ImmutableStack<object> Value { get; set; }
        }

        private static ImmutableStack<object> CurrentScopeStack {
            get { return _currentScopeStack.Value?.Value ?? ImmutableStack.Create<object>(); }
            set { _currentScopeStack.Value = new Wrapper { Value = value }; }
        }

        private static IDisposable Push(object state) {
            CurrentScopeStack = CurrentScopeStack.Push(state);
            return new DisposableAction(Pop);
        }

        private static void Pop() {
            CurrentScopeStack = CurrentScopeStack.Pop();
        }
    }
}