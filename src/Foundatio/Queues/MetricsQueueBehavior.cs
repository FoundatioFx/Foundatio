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
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Reporting queue count");

                await Task.WhenAll(
                    _metricsClient.GaugeAsync(GetFullMetricName("count"), stats.Queued),
                    _metricsClient.GaugeAsync(GetFullMetricName("working"), stats.Working),
                    _metricsClient.GaugeAsync(GetFullMetricName("deadletter"), stats.Deadletter)
                ).AnyContext();
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Error reporting queue metrics.");
            }

            return null;
        }

        protected override Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            var tasks = new List<Task>(2);
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            string subMetricName = GetSubMetricName(enqueuedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.CounterAsync(GetFullMetricName(subMetricName, "enqueued")));

            tasks.Add(_metricsClient.CounterAsync(GetFullMetricName("enqueued")));
            return Task.WhenAll(tasks);
        }

        protected override Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            var tasks = new List<Task>(4);
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            var metadata = dequeuedEventArgs.Entry as IQueueEntryMetadata;
            string subMetricName = GetSubMetricName(dequeuedEventArgs.Entry.Value);

            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.CounterAsync(GetFullMetricName(subMetricName, "dequeued")));
            tasks.Add(_metricsClient.CounterAsync(GetFullMetricName("dequeued")));

            if (metadata == null || metadata.EnqueuedTimeUtc == DateTime.MinValue || metadata.DequeuedTimeUtc == DateTime.MinValue)
                return Task.WhenAll(tasks);

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            var time = (int)(end - start).TotalMilliseconds;

            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.TimerAsync(GetFullMetricName(subMetricName, "queuetime"), time));
            tasks.Add(_metricsClient.TimerAsync(GetFullMetricName("queuetime"), time));
            return Task.WhenAll(tasks);
        }

        protected override Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            if (!(completedEventArgs.Entry is IQueueEntryMetadata metadata))
                return Task.CompletedTask;

            var tasks = new List<Task>(4);
            string subMetricName = GetSubMetricName(completedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.CounterAsync(GetFullMetricName(subMetricName, "completed")));
            tasks.Add(_metricsClient.CounterAsync(GetFullMetricName("completed")));

            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.TimerAsync(GetFullMetricName(subMetricName, "processtime"), time));
            tasks.Add(_metricsClient.TimerAsync(GetFullMetricName("processtime"), time));
            return Task.WhenAll(tasks);
        }

        protected override Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            if (!(abandonedEventArgs.Entry is IQueueEntryMetadata metadata))
                return Task.CompletedTask;

            var tasks = new List<Task>(4);
            string subMetricName = GetSubMetricName(abandonedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.CounterAsync(GetFullMetricName(subMetricName, "abandoned")));
            tasks.Add(_metricsClient.CounterAsync(GetFullMetricName("abandoned")));

            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(subMetricName))
                tasks.Add(_metricsClient.TimerAsync(GetFullMetricName(subMetricName, "processtime"), time));
            tasks.Add(_metricsClient.TimerAsync(GetFullMetricName("processtime"), time));
            return Task.WhenAll(tasks);
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