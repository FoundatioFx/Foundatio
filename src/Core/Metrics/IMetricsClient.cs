using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public interface IMetricsClient : IDisposable {
        Task CounterAsync(string name, int value = 1);
        Task GaugeAsync(string name, double value);
        Task TimerAsync(string name, int milliseconds);
    }

    public interface IBufferedMetricsClient : IMetricsClient {
        Task FlushAsync();
    }

    public static class MetricsClientExtensions {
        public static IAsyncDisposable StartTimer(this IMetricsClient client, string name) {
            return new MetricTimer(name, client);
        }

        public static async Task TimeAsync(this IMetricsClient client, Func<Task> action, string name) {
            await Async.Using(client.StartTimer(name), action).AnyContext();
        }

        public static Task TimeAsync(this IMetricsClient client, Action action, string name) {
            return Async.Using(client.StartTimer(name), action);
        }

        public static Task<T> TimeAsync<T>(this IMetricsClient client, Func<Task<T>> func, string name) {
            return Async.Using(client.StartTimer(name), func);
        }
    }
}