using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching {
    public class SharedCacheClientOptions {
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public interface ISharedCacheClientOptionsBuilder : IOptionsBuilder {}

    public static class SharedCacheClientOptionsExtensions {
        public static T LoggerFactory<T>(this T builder, ILoggerFactory loggerFactory) where T: ISharedCacheClientOptionsBuilder {
            builder.Target<SharedCacheClientOptions>().LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return (T)builder;
        }
    }
}