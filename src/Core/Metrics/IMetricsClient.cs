using System;
using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public interface IMetricsClient : IDisposable {
        Task CounterAsync(string statName, int value = 1);
        Task GaugeAsync(string statName, double value);
        Task TimerAsync(string statName, long milliseconds);
    }

    public static class MetricsClientExtensions
    {
        public static void Counter(this IMetricsClient client, string statName, int value = 1) {
            client.CounterAsync(statName, value).Wait();
        }

        public static void Gauge(this IMetricsClient client, string statName, double value) {
            client.GaugeAsync(statName, value).Wait();
        }

        public static void Timer(this IMetricsClient client, string statName, long milliseconds) {
            client.TimerAsync(statName, milliseconds).Wait();
        }

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