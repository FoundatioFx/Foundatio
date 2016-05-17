using System;
using NLog;

namespace Foundatio.Logging.NLog {
    public class NLogLoggerProvider : ILoggerProvider {
        private readonly Action<object, object[], LogEventInfo> _populateAdditionalLogEventInfo;

        public NLogLoggerProvider() {}

        public NLogLoggerProvider(Action<object, object[], LogEventInfo> populateAdditionalLogEventInfo) {
            _populateAdditionalLogEventInfo = populateAdditionalLogEventInfo;
        }

        public ILogger CreateLogger(string categoryName) {
            return new NLogLogger(LogManager.GetLogger(categoryName), _populateAdditionalLogEventInfo);
        }
    }
}
