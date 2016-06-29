using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly string _metricsPrefix;
        private readonly IMetricsClient _metricsClient;
        private readonly ILogger _logger;
        private readonly ScheduledTimer _timer;

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null, ILoggerFactory loggerFactory = null, TimeSpan? reportCountsInterval = null) {
            _logger = loggerFactory.CreateLogger<MetricsQueueBehavior<T>>();
            _metricsClient = metrics ?? NullMetricsClient.Instance;

            if (!reportCountsInterval.HasValue)
                reportCountsInterval = TimeSpan.FromMilliseconds(500);

            if (!String.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();
            _metricsPrefix = metricsPrefix;
            _timer = new ScheduledTimer(ReportQueueCountAsync, minimumIntervalTime: reportCountsInterval, loggerFactory: loggerFactory);
        }

        private async Task<DateTime?> ReportQueueCountAsync() {
            var stats = await _queue.GetQueueStatsAsync().AnyContext();
            _logger.Trace("Reporting queue count");

            await _metricsClient.GaugeAsync(GetFullMetricName("count"), stats.Queued).AnyContext();
            await _metricsClient.GaugeAsync(GetFullMetricName("working"), stats.Working).AnyContext();
            await _metricsClient.GaugeAsync(GetFullMetricName("deadletter"), stats.Deadletter).AnyContext();

            return null;
        }

        protected override async Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            await base.OnEnqueued(sender, enqueuedEventArgs).AnyContext();
            _timer.ScheduleNext();

            string customMetricName = GetCustomMetricName(enqueuedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "enqueued")).AnyContext();

            await _metricsClient.CounterAsync(GetFullMetricName("enqueued")).AnyContext();
        }

        protected override async Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            await base.OnDequeued(sender, dequeuedEventArgs).AnyContext();
            _timer.ScheduleNext();

            var metadata = dequeuedEventArgs.Entry as IQueueEntryMetadata;
            string customMetricName = GetCustomMetricName(dequeuedEventArgs.Entry.Value);

            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "dequeued")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("dequeued")).AnyContext();

            if (metadata == null || metadata.EnqueuedTimeUtc == DateTime.MinValue || metadata.DequeuedTimeUtc == DateTime.MinValue)
                return;

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            var time = (int)(end - start).TotalMilliseconds;

            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(customMetricName, "queuetime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("queuetime"), time).AnyContext();
        }

        protected override async Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            await base.OnCompleted(sender, completedEventArgs).AnyContext();
            _timer.ScheduleNext();

            var metadata = completedEventArgs.Entry as IQueueEntryMetadata;
            if (metadata == null)
                return;

            string customMetricName = GetCustomMetricName(completedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "completed")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("completed")).AnyContext();

            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(customMetricName, "processtime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("processtime"), time).AnyContext();
        }

        protected override async Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            await base.OnAbandoned(sender, abandonedEventArgs).AnyContext();
            _timer.ScheduleNext();

            var metadata = abandonedEventArgs.Entry as IQueueEntryMetadata;
            if (metadata == null)
                return;

            string customMetricName = GetCustomMetricName(abandonedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "abandoned")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("abandoned")).AnyContext();

            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(customMetricName, "processtime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("processtime"), time).AnyContext();
        }

        protected string GetCustomMetricName(T data) {
            var haveStatName = data as IHaveMetricName;
            return haveStatName?.GetMetricName();
        }

        protected string GetFullMetricName(string name) {
            return String.Concat(_metricsPrefix, ".", name);
        }

        protected string GetFullMetricName(string customMetricName, string name) {
            return String.IsNullOrEmpty(customMetricName) ? GetFullMetricName(name) : String.Concat(_metricsPrefix, ".", customMetricName.ToLower(), ".", name);
        }

        public override void Dispose() {
            _timer?.Dispose();
            base.Dispose();
        }
    }
}