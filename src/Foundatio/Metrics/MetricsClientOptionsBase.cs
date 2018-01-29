using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Metrics {
    public class SharedMetricsClientOptions {
        public bool Buffered { get; set; } = true;
        public string Prefix { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public interface ISharedMetricsClientOptionsBuilder : IOptionsBuilder {}

    public static class MetricsClientOptionsBuilderExtensions {
        public static T Buffered<T>(this T builder, bool buffered) where T: ISharedMetricsClientOptionsBuilder {
            builder.Target<SharedMetricsClientOptions>().Buffered = buffered;
            return (T)builder;
        }

        public static T Prefix<T>(this T builder, string prefix) where T: ISharedMetricsClientOptionsBuilder {
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentNullException(nameof(prefix));
            builder.Target<SharedMetricsClientOptions>().Prefix = prefix;
            return (T)builder;
        }

        public static T LoggerFactory<T>(this T builder, ILoggerFactory loggerFactory) where T: ISharedMetricsClientOptionsBuilder {
            builder.Target<SharedMetricsClientOptions>().LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return (T)builder;
        }

        public static T EnableBuffer<T>(this T builder) where T: ISharedMetricsClientOptionsBuilder => builder.Buffered(true);

        public static T DisableBuffer<T>(this T builder) where T: ISharedMetricsClientOptionsBuilder => builder.Buffered(false);
    }
}