using System;
using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public interface IMetricsClient : IDisposable {
        Task CounterAsync(string statName, int value = 1);
        Task GaugeAsync(string statName, double value);
        Task TimerAsync(string statName, int milliseconds);
    }

    public static class MetricsClientExtensions {
        public static IDisposable StartTimer(this IMetricsClient client, string statName) {
            return new MetricTimer(statName, client);
        }

        public static void Time(this IMetricsClient client, Action action, string statName) {
            using (client.StartTimer(statName))
                action();
        }

        public static T Time<T>(this IMetricsClient client, Func<T> func, string statName) {
            using (client.StartTimer(statName))
                return func();
        }
    }
}