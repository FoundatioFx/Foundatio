using System;
using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public interface IMetricsClient : IDisposable {
        Task CounterAsync(string name, int value = 1);
        Task GaugeAsync(string name, double value);
        Task TimerAsync(string name, int milliseconds);
    }

    public static class MetricsClientExtensions {
        public static IDisposable StartTimer(this IMetricsClient client, string name) {
            return new MetricTimer(name, client);
        }

        public static void Time(this IMetricsClient client, Action action, string name) {
            using (client.StartTimer(name))
                action();
        }

        public static T Time<T>(this IMetricsClient client, Func<T> func, string name) {
            using (client.StartTimer(name))
                return func();
        }
    }
}