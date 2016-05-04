using System;

namespace Foundatio.Logging {
    public static class BuilderExtensions {
        public static ILogBuilder Level(this ILogger logger, LogLevel logLevel) {
            if (!logger.IsEnabled(logLevel))
                return NullLogBuilder.Instance;

            return new LogBuilder(logLevel, logger);
        }

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
    }
}
