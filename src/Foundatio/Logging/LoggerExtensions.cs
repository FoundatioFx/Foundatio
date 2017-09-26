using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Logging {
    public static class LoggerExtensions {
        public static ILogger GetLogger(this object target) {
            return target is IHaveLogger accessor ? accessor.Logger : NullLogger.Instance;
        }

        public static IDisposable BeginScope(this ILogger logger, string key, object value) {
            return logger.BeginScope(new Dictionary<string, object> { { key, value } });
        }
    }
}