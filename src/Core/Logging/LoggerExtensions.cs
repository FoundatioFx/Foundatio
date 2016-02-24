using System;

namespace Foundatio.Logging {
    public static class LoggerExtensions {
        public static ILogBuilder Trace(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Trace))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Trace, logger);
        }

        public static ILogBuilder Debug(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Debug))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Debug, logger);
        }

        public static ILogBuilder Info(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Information))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Information, logger);
        }

        public static ILogBuilder Warn(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Warning))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Warning, logger);
        }

        public static ILogBuilder Error(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Error))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Error, logger);
        }

        public static ILogBuilder Critical(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Critical))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Critical, logger);
        }

        public static IDisposable BeginScope(this ILogger logger, string scope) {
            return logger.BeginScope(s => s, scope);
        }
    }
}