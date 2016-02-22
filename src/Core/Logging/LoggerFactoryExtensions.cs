using System;
using Foundatio.Logging.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace Foundatio.Logging {
    public static class LoggerFactoryExtensions {
        // TODO: This goes away in RC2
        public static ILogger CreateLogger(this ILoggerFactory loggerFactory, Type type) {
            return loggerFactory?.CreateLogger(TypeNameHelper.GetTypeDisplayName(type)) ?? NullLogger.Instance;
        }
    }
}