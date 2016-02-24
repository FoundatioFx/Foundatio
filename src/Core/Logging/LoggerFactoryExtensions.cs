using System;
using Foundatio.Logging.Abstractions.Internal;

namespace Foundatio.Logging {
    public static class LoggerFactoryExtensions {
        public static ILogger CreateLogger<T>(this ILoggerFactory loggerFactory) {
            return loggerFactory?.CreateLogger(typeof(T)) ?? NullLogger.Instance;
        }

        public static ILogger CreateLogger(this ILoggerFactory loggerFactory, Type type) {
            return loggerFactory?.CreateLogger(TypeNameHelper.GetTypeDisplayName(type)) ?? NullLogger.Instance;
        }
    }
}