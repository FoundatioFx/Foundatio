using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Queues {
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly string _metricsPrefix;
        private readonly IMetricsClient _metricsClient;
        private readonly ILogger _logger;
        private readonly ScheduledTimer _timer;
        private readonly TimeSpan _reportInterval;

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null, TimeSpan? reportCountsInterval = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<MetricsQueueBehavior<T>>() ?? NullLogger<MetricsQueueBehavior<T>>.Instance;
            _metricsClient = metrics ?? NullMetricsClient.Instance;

            if (!reportCountsInterval.HasValue)
                reportCountsInterval = TimeSpan.FromMilliseconds(500);

            _reportInterval = reportCountsInterval.Value > TimeSpan.Zero ? reportCountsInterval.Value : TimeSpan.FromMilliseconds(250);
            if (!String.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();
            _metricsPrefix = metricsPrefix;
            _timer = new ScheduledTimer(ReportQueueCountAsync, loggerFactory: loggerFactory);
        }

        private async Task<DateTime?> ReportQueueCountAsync() {
            try {
                var stats = await _queue.GetQueueStatsAsync().AnyContext();
                _logger.LogTrace("Reporting queue count");

                _metricsClient.Gauge(GetFullMetricName("count"), stats.Queued);
                _metricsClient.Gauge(GetFullMetricName("working"), stats.Working);
                _metricsClient.Gauge(GetFullMetricName("deadletter"), stats.Deadletter);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error reporting queue metrics.");
            }

            return null;
        }

        protected override Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            string subMetricName = GetSubMetricName(enqueuedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Counter(GetFullMetricName(subMetricName, "enqueued"));

            _metricsClient.Counter(GetFullMetricName("enqueued"));
            return Task.CompletedTask;
        }

        protected override Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            var metadata = dequeuedEventArgs.Entry as IQueueEntryMetadata;
            string subMetricName = GetSubMetricName(dequeuedEventArgs.Entry.Value);

            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Counter(GetFullMetricName(subMetricName, "dequeued"));
            _metricsClient.Counter(GetFullMetricName("dequeued"));

            if (metadata == null || metadata.EnqueuedTimeUtc == DateTime.MinValue || metadata.DequeuedTimeUtc == DateTime.MinValue)
                return Task.CompletedTask;

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            int time = (int)(end - start).TotalMilliseconds;

            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Timer(GetFullMetricName(subMetricName, "queuetime"), time);
            _metricsClient.Timer(GetFullMetricName("queuetime"), time);

            return Task.CompletedTask;
        }

        protected override Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            if (!(completedEventArgs.Entry is IQueueEntryMetadata metadata))
                return Task.CompletedTask;

            string subMetricName = GetSubMetricName(completedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Counter(GetFullMetricName(subMetricName, "completed"));
            _metricsClient.Counter(GetFullMetricName("completed"));

            int time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Timer(GetFullMetricName(subMetricName, "processtime"), time);
            _metricsClient.Timer(GetFullMetricName("processtime"), time);
            return Task.CompletedTask;
        }

        protected override Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            if (!(abandonedEventArgs.Entry is IQueueEntryMetadata metadata))
                return Task.CompletedTask;

            string subMetricName = GetSubMetricName(abandonedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Counter(GetFullMetricName(subMetricName, "abandoned"));
            _metricsClient.Counter(GetFullMetricName("abandoned"));

            int time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(subMetricName))
                _metricsClient.Timer(GetFullMetricName(subMetricName, "processtime"), time);
            _metricsClient.Timer(GetFullMetricName("processtime"), time);
            return Task.CompletedTask;
        }

        protected string GetSubMetricName(T data) {
            var haveStatName = data as IHaveSubMetricName;
            return haveStatName?.GetSubMetricName();
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