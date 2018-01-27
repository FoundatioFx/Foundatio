using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Metrics {
    public abstract class MetricsClientOptionsBase {
        public bool Buffered { get; set; } = true;
        public string Prefix { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class MetricsClientOptionsExtensions {
        public static IOptionsBuilder<MetricsClientOptionsBase> Prefix(this IOptionsBuilder<MetricsClientOptionsBase> builder, string prefix) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentNullException(nameof(prefix));
            builder.Target.Prefix = prefix;
            return builder;
        }

        public static IOptionsBuilder<MetricsClientOptionsBase> LoggerFactory(this IOptionsBuilder<MetricsClientOptionsBase> builder, ILoggerFactory loggerFactory) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return builder;
        }

        public static IOptionsBuilder<MetricsClientOptionsBase> Buffered(this IOptionsBuilder<MetricsClientOptionsBase> builder, bool enableBuffer) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Buffered = enableBuffer;
            return builder;
        }

        public static IOptionsBuilder<MetricsClientOptionsBase> EnableBuffer(this IOptionsBuilder<MetricsClientOptionsBase> options) => options.Buffered(true);

        public static IOptionsBuilder<MetricsClientOptionsBase> DisableBuffer(this IOptionsBuilder<MetricsClientOptionsBase> options) => options.Buffered(false);
    }
}