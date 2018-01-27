using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching {
    public abstract class CacheClientOptionsBase {
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class CacheClientOptionsExtensions {
        public static CacheClientOptionsBase WithLoggerFactory(this CacheClientOptionsBase options, ILoggerFactory loggerFactory) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return options;
        }
    }
}