using System;

namespace Foundatio.Metrics {
    public class SharedMetricsClientOptions : SharedOptions {
        public bool Buffered { get; set; } = true;
        public string Prefix { get; set; }
    }

    public class SharedMetricsClientOptionsBuilder<TOption,TBuilder> : SharedOptionsBuilder<TOption, TBuilder>
        where TOption: SharedMetricsClientOptions ,new()
        where TBuilder: SharedMetricsClientOptionsBuilder<TOption, TBuilder> {
        public TBuilder Buffered(bool buffered) {
            Target.Buffered = buffered;
            return (TBuilder)this;
        }

        public TBuilder Prefix(string prefix) {
            if (String.IsNullOrEmpty(prefix))
                throw new ArgumentNullException(nameof(prefix));
            Target.Prefix = prefix;
            return (TBuilder)this;
        }

        public TBuilder EnableBuffer() => Buffered(true);

        public TBuilder DisableBuffer() => Buffered(false);
    }
}