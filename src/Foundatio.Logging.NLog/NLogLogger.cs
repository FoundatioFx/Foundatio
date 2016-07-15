using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Foundatio.Utility;
using NLog;

namespace Foundatio.Logging.NLog {
    internal class NLogLogger : ILogger {
        private readonly global::NLog.Logger _logger;
        private readonly Action<object, object[], LogEventInfo> _populateAdditionalLogEventInfo;

        public NLogLogger(global::NLog.Logger logger, Action<object, object[], LogEventInfo> populateAdditionalLogEventInfo = null) {
            _logger = logger;
            _populateAdditionalLogEventInfo = populateAdditionalLogEventInfo;
        }

        // TODO: callsite showing the framework logging classes/methods
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            var nLogLogLevel = ConvertLogLevel(logLevel);
            if (!IsEnabled(nLogLogLevel))
                return;

            string message = formatter != null ? formatter(state, exception) : state.ToString();
            if (string.IsNullOrEmpty(message))
                return;

            var eventInfo = global::NLog.LogEventInfo.Create(nLogLogLevel, _logger.Name, message);
            eventInfo.Exception = exception;
            if (eventId.Id != 0)
                eventInfo.Properties["EventId"] = eventId;

            var logData = state as LogData;
            if (logData != null) {
                eventInfo.Properties["CallerMemberName"] = logData.MemberName;
                eventInfo.Properties["CallerFilePath"] = logData.FilePath;
                eventInfo.Properties["CallerLineNumber"] = logData.LineNumber;

                foreach (var property in logData.Properties)
                    eventInfo.Properties[property.Key] = property.Value;
            } else {
                var logDictionary = state as IDictionary<string, object>;
                if (logDictionary != null) {
                    foreach (var property in logDictionary)
                        eventInfo.Properties[property.Key] = property.Value;
                }
            }

            var scopes = CurrentScopeStack.Reverse().ToArray();
            foreach (var scope in scopes) {
                var scopeData = scope as IDictionary<string, object>;
                if (scopeData == null)
                    continue;

                foreach (var property in scopeData)
                    eventInfo.Properties[property.Key] = property.Value;
            }

            _populateAdditionalLogEventInfo?.Invoke(state, scopes, eventInfo);

            _logger.Log(eventInfo);
        }

        public bool IsEnabled(LogLevel logLevel) {
            var convertLogLevel = ConvertLogLevel(logLevel);
            return IsEnabled(convertLogLevel);
        }

        private bool IsEnabled(global::NLog.LogLevel logLevel) {
            return _logger.IsEnabled(logLevel);
        }

        private static global::NLog.LogLevel ConvertLogLevel(LogLevel logLevel) {
            switch (logLevel) {
                case LogLevel.Debug:
                    return global::NLog.LogLevel.Debug;
                case LogLevel.Trace:
                    return global::NLog.LogLevel.Trace;
                case LogLevel.Information:
                    return global::NLog.LogLevel.Info;
                case LogLevel.Warning:
                    return global::NLog.LogLevel.Warn;
                case LogLevel.Error:
                    return global::NLog.LogLevel.Error;
                case LogLevel.Critical:
                    return global::NLog.LogLevel.Fatal;
                case LogLevel.None:
                    return global::NLog.LogLevel.Off;
                default:
                    return global::NLog.LogLevel.Debug;
            }
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
