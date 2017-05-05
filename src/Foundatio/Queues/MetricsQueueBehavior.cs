using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly string _metricsPrefix;
        private readonly IMetricsClient _metricsClient;
        private readonly ILogger _logger;
        private readonly ScheduledTimer _timer;
        private readonly TimeSpan _reportInterval;

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null, TimeSpan? reportCountsInterval = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger<MetricsQueueBehavior<T>>();
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
                _logger.Trace("Reporting queue count");

                await _metricsClient.GaugeAsync(GetFullMetricName("count"), stats.Queued).AnyContext();
                await _metricsClient.GaugeAsync(GetFullMetricName("working"), stats.Working).AnyContext();
                await _metricsClient.GaugeAsync(GetFullMetricName("deadletter"), stats.Deadletter).AnyContext();
            } catch (Exception ex) {
                _logger.Error(ex, "Error reporting queue metrics.");
            }

            return null;
        }

        protected override async Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            string subMetricName = GetSubMetricName(enqueuedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(subMetricName, "enqueued")).AnyContext();

            await _metricsClient.CounterAsync(GetFullMetricName("enqueued")).AnyContext();
        }

        protected override async Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            var metadata = dequeuedEventArgs.Entry as IQueueEntryMetadata;
            string subMetricName = GetSubMetricName(dequeuedEventArgs.Entry.Value);

            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(subMetricName, "dequeued")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("dequeued")).AnyContext();

            if (metadata == null || metadata.EnqueuedTimeUtc == DateTime.MinValue || metadata.DequeuedTimeUtc == DateTime.MinValue)
                return;

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            var time = (int)(end - start).TotalMilliseconds;

            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(subMetricName, "queuetime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("queuetime"), time).AnyContext();
        }

        protected override async Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            var metadata = completedEventArgs.Entry as IQueueEntryMetadata;
            if (metadata == null)
                return;

            string subMetricName = GetSubMetricName(completedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(subMetricName, "completed")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("completed")).AnyContext();

            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(subMetricName, "processtime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("processtime"), time).AnyContext();
        }

        protected override async Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            _timer.ScheduleNext(SystemClock.UtcNow.Add(_reportInterval));

            var metadata = abandonedEventArgs.Entry as IQueueEntryMetadata;
            if (metadata == null)
                return;

            string subMetricName = GetSubMetricName(abandonedEventArgs.Entry.Value);
            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(subMetricName, "abandoned")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("abandoned")).AnyContext();

            var time = (int)metadata.ProcessingTime.TotalMilliseconds;
            if (!String.IsNullOrEmpty(subMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(subMetricName, "processtime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("processtime"), time).AnyContext();
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