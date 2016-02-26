using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using Foundatio.Utility;

namespace Foundatio.Logging.NLog {
    internal class NLogLogger : ILogger {
        private readonly global::NLog.Logger _logger;

        public NLogLogger(global::NLog.Logger logger) {
            _logger = logger;
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

            // TODO: Check for dictionary and add more properties

            foreach (var scope in CurrentScopeStack.ToArray()) {
                // TODO: Check scopes for dictionary and add more properties
            }

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

        private static readonly string _name = Guid.NewGuid().ToString("N");

        private sealed class Wrapper : MarshalByRefObject {
            public ImmutableStack<object> Value { get; set; }
        }

        private static ImmutableStack<object> CurrentScopeStack {
            get {
                var ret = CallContext.LogicalGetData(_name) as Wrapper;
                return ret == null ? ImmutableStack.Create<object>() : ret.Value;
            }
            set {
                CallContext.LogicalSetData(_name, new Wrapper { Value = value });
            }
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
