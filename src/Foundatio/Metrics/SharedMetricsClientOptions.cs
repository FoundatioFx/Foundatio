using System;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Metrics {
    public class SharedMetricsClientOptions : SharedOptions {
        public bool Buffered { get; set; } = true;
        public string Prefix { get; set; }
    }

    public interface ISharedMetricsClientOptionsBuilder : ISharedOptionsBuilder {}

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

        public static T EnableBuffer<T>(this T builder) where T: ISharedMetricsClientOptionsBuilder => builder.Buffered(true);

        public static T DisableBuffer<T>(this T builder) where T: ISharedMetricsClientOptionsBuilder => builder.Buffered(false);
    }
}