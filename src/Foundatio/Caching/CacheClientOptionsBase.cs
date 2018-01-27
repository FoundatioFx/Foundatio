using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching {
    public abstract class CacheClientOptionsBase {
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class CacheClientOptionsExtensions {
        public static IOptionsBuilder<CacheClientOptionsBase> LoggerFactory(this IOptionsBuilder<CacheClientOptionsBase> options, ILoggerFactory loggerFactory) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return options;
        }
    }
}