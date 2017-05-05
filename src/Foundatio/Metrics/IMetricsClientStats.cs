using System;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Queues;

namespace Foundatio.Metrics {
    public interface IMetricsClientStats {
        Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? utcStart = null, DateTime? utcEnd = null, int dataPoints = 20);
        Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? utcStart = null, DateTime? utcEnd = null, int dataPoints = 20);
        Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? utcStart = null, DateTime? utcEnd = null, int dataPoints = 20);
    }

    public static class MetricsClientStatsExtensions {
        public static async Task<long> GetCounterCountAsync(this IMetricsClientStats stats, string name, DateTime? utcStart = null, DateTime? utcEnd = null) {
            var result = await stats.GetCounterStatsAsync(name, utcStart, utcEnd, 1).AnyContext();
            return result.Count;
        }

        public static async Task<double> GetLastGaugeValueAsync(this IMetricsClientStats stats, string name, DateTime? utcStart = null, DateTime? utcEnd = null) {
            var result = await stats.GetGaugeStatsAsync(name, utcStart, utcEnd, 1).AnyContext();
            return result.Last;
        }

        public static async Task<QueueStatSummary> GetQueueStatsAsync(this IMetricsClientStats stats, string name, string subMetricName = null, DateTime? utcStart = null, DateTime? utcEnd = null, int dataPoints = 20) {
            if (subMetricName == null)
                subMetricName = String.Empty;
            else
                subMetricName = "." + subMetricName;

            var countTask = stats.GetGaugeStatsAsync($"{name}.count", utcStart, utcEnd, dataPoints);
            var workingTask = stats.GetGaugeStatsAsync($"{name}.working", utcStart, utcEnd, dataPoints);
            var deadletterTask = stats.GetGaugeStatsAsync($"{name}.deadletter", utcStart, utcEnd, dataPoints);
            var enqueuedTask = stats.GetCounterStatsAsync($"{name}{subMetricName}.enqueued", utcStart, utcEnd, dataPoints);
            var queueTimeTask = stats.GetTimerStatsAsync($"{name}{subMetricName}.queuetime", utcStart, utcEnd, dataPoints);
            var dequeuedTask = stats.GetCounterStatsAsync($"{name}{subMetricName}.dequeued", utcStart, utcEnd, dataPoints);
            var completedTask = stats.GetCounterStatsAsync($"{name}{subMetricName}.completed", utcStart, utcEnd, dataPoints);
            var abandonedTask = stats.GetCounterStatsAsync($"{name}{subMetricName}.abandoned", utcStart, utcEnd, dataPoints);
            var processTimeTask = stats.GetTimerStatsAsync($"{name}{subMetricName}.processtime", utcStart, utcEnd, dataPoints);

            await Task.WhenAll(countTask, workingTask, deadletterTask, enqueuedTask, queueTimeTask, dequeuedTask, completedTask, abandonedTask, processTimeTask).AnyContext();

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