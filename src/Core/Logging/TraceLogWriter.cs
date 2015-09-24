using System;
using System.Diagnostics;

namespace Foundatio.Logging {
    /// <summary>
    /// A system trace log writer
    /// </summary>
    public class TraceLogWriter : ILogWriter {
        private readonly static Lazy<TraceSource> _traceSource;

        /// <summary>
        /// Initializes the <see cref="TraceLogWriter"/> class.
        /// </summary>
        static TraceLogWriter() {
            _traceSource = new Lazy<TraceSource>(() => new TraceSource(typeof(Logger).FullName, SourceLevels.Information));
        }

        /// <summary>
        /// Writes the specified LogData to the underlying logger.
        /// </summary>
        /// <param name="logData">The log data.</param>
        public void WriteLog(LogData logData) {
            var eventType = ToEventType(logData.LogLevel);
            if (logData.Parameters != null && logData.Parameters.Length > 0)
                _traceSource.Value.TraceEvent(eventType, 1, logData.Message, logData.Parameters);
            else
                _traceSource.Value.TraceEvent(eventType, 1, logData.Message);
        }

        private TraceEventType ToEventType(LogLevel logLevel) {
            switch (logLevel) {
                case LogLevel.Trace:
                    return TraceEventType.Verbose;
                case LogLevel.Debug:
                    return TraceEventType.Verbose;
                case LogLevel.Info:
                    return TraceEventType.Information;
                case LogLevel.Warn:
                    return TraceEventType.Warning;
                case LogLevel.Error:
                    return TraceEventType.Error;
                case LogLevel.Fatal:
                    return TraceEventType.Critical;
                default:
                    return TraceEventType.Verbose;
            }
        }
    }
}