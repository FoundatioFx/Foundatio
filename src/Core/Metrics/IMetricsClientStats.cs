using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Queues;

namespace Foundatio.Metrics {
    public interface IMetricsClientStats {
        Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20);
        Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20);
        Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20);
    }

    public static class MetricsClientStatsExtensions {
        public static async Task<long> GetCounterCountAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var result = await stats.GetCounterStatsAsync(name, start, end, 1).AnyContext();
            return result.Count;
        }

        public static async Task<double> GetLastGaugeValueAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var result = await stats.GetGaugeStatsAsync(name, start, end, 1).AnyContext();
            return result.Last;
        }

        public static async Task<QueueStatSummary> GetQueueStatsAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20) {
            var countTask = stats.GetGaugeStatsAsync($"{name}.count", start, end, dataPoints);
            var workingTask = stats.GetGaugeStatsAsync($"{name}.working", start, end, dataPoints);
            var deadletterTask = stats.GetGaugeStatsAsync($"{name}.deadletter", start, end, dataPoints);
            var enqueuedTask = stats.GetCounterStatsAsync($"{name}.enqueued", start, end, dataPoints);
            var queueTimeTask = stats.GetTimerStatsAsync($"{name}.queuetime", start, end, dataPoints);
            var dequeuedTask = stats.GetCounterStatsAsync($"{name}.dequeued", start, end, dataPoints);
            var completedTask = stats.GetCounterStatsAsync($"{name}.completed", start, end, dataPoints);
            var abandonedTask = stats.GetCounterStatsAsync($"{name}.abandoned", start, end, dataPoints);
            var processTimeTask = stats.GetTimerStatsAsync($"{name}.processtime", start, end, dataPoints);

            await Task.WhenAll(countTask, workingTask, deadletterTask, enqueuedTask, queueTimeTask, dequeuedTask, completedTask, abandonedTask, processTimeTask);

            return new QueueStatSummary {
                Count = countTask.Result,
                Working = workingTask.Result,
                Deadletter = deadletterTask.Result,
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