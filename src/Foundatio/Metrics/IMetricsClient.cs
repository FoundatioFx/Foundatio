using System;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public interface IMetricsClient : IDisposable {
        void Counter(string name, int value = 1);
        void Gauge(string name, double value);
        void Timer(string name, int milliseconds);
    }

    public interface IBufferedMetricsClient : IMetricsClient {
        Task FlushAsync();
    }

    public static class MetricsClientExtensions {
        public static IDisposable StartTimer(this IMetricsClient client, string name) {
            return new MetricTimer(name, client);
        }

        public static async Task TimeAsync(this IMetricsClient client, Func<Task> action, string name) {
            using (client.StartTimer(name))
                await action().AnyContext();
        }

        public static void Time(this IMetricsClient client, Action action, string name) {
            using (client.StartTimer(name))
                action();
        }

        public static async Task<T> TimeAsync<T>(this IMetricsClient client, Func<Task<T>> func, string name) {
            using (client.StartTimer(name))
                return await func().AnyContext();
        }
    }
}