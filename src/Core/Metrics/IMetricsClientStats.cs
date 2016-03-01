using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Queues;

namespace Foundatio.Metrics {
    public interface IMetricsClientStats {
        Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = null, DateTime? end = null);
        Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? start = null, DateTime? end = null);
        Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = null, DateTime? end = null);
    }

    public static class MetricsClientStatsExtensions {
        public static async Task<long> GetCounterCountAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var result = await stats.GetCounterStatsAsync(name, start, end).AnyContext();
            return result.Count;
        }

        public static async Task<double> GetLastGaugeValueAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var result = await stats.GetGaugeStatsAsync(name, start, end).AnyContext();
            return result.Last;
        }

        public static async Task<QueueStatSummary> GetQueueStatsAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var countTask = stats.GetGaugeStatsAsync($"{name}.counter", start, end);
            var enqueuedTask = stats.GetCounterStatsAsync($"{name}.enqueued", start, end);
            var queueTimeTask = stats.GetTimerStatsAsync($"{name}.queuetime", start, end);
            var dequeuedTask = stats.GetCounterStatsAsync($"{name}.dequeued", start, end);
            var completedTask = stats.GetCounterStatsAsync($"{name}.completed", start, end);
            var abandonedTask = stats.GetCounterStatsAsync($"{name}.abandoned", start, end);
            var processTimeTask = stats.GetTimerStatsAsync($"{name}.processtime", start, end);

            await Task.WhenAll(countTask, enqueuedTask, queueTimeTask, dequeuedTask, completedTask, abandonedTask, processTimeTask);

            return new QueueStatSummary {
                Count = countTask.Result,
                Enqueued = enqueuedTask.Result,
                QueueTime = queueTimeTask.Result,
                Dequeued = dequeuedTask.Result,
                Completed = completedTask.Result,
                Abandoned = abandonedTask.Result,
                ProcessTime = processTimeTask.Result
            };
        }
    }
}