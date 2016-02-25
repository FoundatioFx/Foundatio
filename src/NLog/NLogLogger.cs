using System;

namespace Foundatio.Logging.NLog {
    public class NLogLogger : ILogger {
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
            eventInfo.Properties["EventId"] = eventId;
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

            // TODO: not working with async
            return global::NLog.NestedDiagnosticsContext.Push(state.ToString());
        }
    }
}
