using System;
using NLog;

namespace Foundatio.Logging.NLog {
    public class NLogLoggerProvider : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) {
            return new NLogLogger(LogManager.GetLogger(categoryName));
        }
    }
}
