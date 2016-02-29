using System;
using Foundatio.Utility;

namespace Foundatio.Logging {
    public static class LoggerFactoryExtensions {
        public static ILogger<T> CreateLogger<T>(this ILoggerFactory loggerFactory) {
            return loggerFactory != null ? new Logger<T>(loggerFactory) : NullLogger<T>.Instance;
        }

        public static ILogger CreateLogger(this ILoggerFactory loggerFactory, Type type) {
            return loggerFactory?.CreateLogger(TypeHelper.GetTypeDisplayName(type)) ?? NullLogger.Instance;
        }

        public static void SetLogLevel<T>(this ILoggerFactory loggerFactory, LogLevel minLogLevel) {
            loggerFactory.SetLogLevel(TypeHelper.GetTypeDisplayName(typeof(T)), minLogLevel);
        }
    }
}