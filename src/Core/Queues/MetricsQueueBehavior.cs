using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Metrics;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly string _metricsPrefix;
        private readonly IMetricsClient _metricsClient;
        private DateTime _nextQueueCountTime = DateTime.MinValue;
        private readonly AsyncLock _countLock = new AsyncLock();
        private readonly ILogger _logger;

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<MetricsQueueBehavior<T>>() ?? NullLogger.Instance;
            _metricsClient = metrics;

            if (!String.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();
            _metricsPrefix = metricsPrefix;
        }

        private async Task ReportQueueCountAsync() {
            if (_nextQueueCountTime > DateTime.UtcNow)
                return;

            using (await _countLock.LockAsync()) {
                if (_nextQueueCountTime > DateTime.UtcNow)
                    return;

                _nextQueueCountTime = DateTime.UtcNow.AddMilliseconds(500);
                var stats = await _queue.GetQueueStatsAsync().AnyContext();
                _logger.Trace().Message("Reporting queue count").Write();

                await _metricsClient.GaugeAsync(GetFullMetricName("count"), stats.Queued).AnyContext();
            }
        }

        protected override async Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            await base.OnEnqueued(sender, enqueuedEventArgs).AnyContext();
            await ReportQueueCountAsync().AnyContext();

            string customMetricName = GetCustomMetricName(enqueuedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "enqueued")).AnyContext();

            await _metricsClient.CounterAsync(GetFullMetricName("enqueued")).AnyContext();
        }

        protected override async Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            await base.OnDequeued(sender, dequeuedEventArgs).AnyContext();
            await ReportQueueCountAsync().AnyContext();

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
            await ReportQueueCountAsync().AnyContext();

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
            await ReportQueueCountAsync().AnyContext();

            var metadata = abandonedEventArgs.Entry as IQueueEntryMetadata;
            if (metadata == null)
                return;

            string customMetricName = GetCustomMetricName(abandonedEventArgs.Entry.Value);
            string counter = GetFullMetricName(customMetricName, "abandoned");
            await _metricsClient.CounterAsync(counter).AnyContext();

            string timer = GetFullMetricName(customMetricName, "abandontime");
            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            await _metricsClient.TimerAsync(timer, time).AnyContext();
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
    }
}