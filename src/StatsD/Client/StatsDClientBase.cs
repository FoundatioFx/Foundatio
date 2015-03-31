using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Foundatio.StatsD {
    public abstract class StatsDClientBase : IStatsDClient {
        protected readonly string _prefix;

        protected StatsDClientBase(string prefix = null) {
            if (!String.IsNullOrEmpty(prefix))
                _prefix = prefix.EndsWith(".") ? prefix : String.Concat(prefix, ".");
        }

        public virtual Task CounterAsync(string statName, int value = 1) {
            return TrySendAsync(BuildMetric("c", statName, value.ToString(CultureInfo.InvariantCulture)));
        }

        public virtual Task GaugeAsync(string statName, double value) {
            return TrySendAsync(BuildMetric("g", statName, value.ToString(CultureInfo.InvariantCulture)));
        }

        public virtual Task TimerAsync(string statName, long milliseconds) {
            return TrySendAsync(BuildMetric("ms", statName, milliseconds.ToString(CultureInfo.InvariantCulture)));
        }

        protected abstract Task TrySendAsync(string metric);

        protected virtual string BuildMetric(string type, string statName, string value) {
            return String.Format("{0}{1}:{2}|{3}", _prefix, statName, value, type);
        }

        public virtual void Dispose() {}
    }
}