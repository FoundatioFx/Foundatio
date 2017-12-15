using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Logging {
    public static class DefaultLoggerFactory {
        public static ILoggerFactory Instance { get; set; } = NullLoggerFactory.Instance;
    }

    public static class DefaultLogger {
        public static ILogger Instance { get; set; } = NullLogger.Instance;
    }
}
