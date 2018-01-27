using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Metrics {
    public abstract class MetricsClientOptionsBase {
        public bool Buffered { get; set; } = true;
        public string Prefix { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class MetricsClientOptionsExtensions {
        public static MetricsClientOptionsBase WithPrefix(this MetricsClientOptionsBase options, string prefix) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentNullException(nameof(prefix));
            options.Prefix = prefix;
            return options;
        }

        public static MetricsClientOptionsBase WithLoggerFactory(this MetricsClientOptionsBase options, ILoggerFactory loggerFactory) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return options;
        }

        public static MetricsClientOptionsBase ShouldBuffer(this MetricsClientOptionsBase options, bool enableBuffer) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Buffered = enableBuffer;
            return options;
        }

        public static MetricsClientOptionsBase EnableBuffer(this MetricsClientOptionsBase options) => options.ShouldBuffer(true);

        public static MetricsClientOptionsBase DisableBuffer(this MetricsClientOptionsBase options) => options.ShouldBuffer(false);
    }
}