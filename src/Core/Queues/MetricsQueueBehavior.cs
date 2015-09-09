using System;
using Foundatio.Extensions;
using Foundatio.Metrics;

namespace Foundatio.Queues {
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly string _metricsPrefix;
        private readonly IMetricsClient _metricsClient;
        private const string CustomMetricNameKey = "CustomMetricName";

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null) {
            _metricsClient = metrics;

            if (!String.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();
            _metricsPrefix = metricsPrefix;
        }

        protected override async void OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            base.OnEnqueued(sender, enqueuedEventArgs);

            string customMetricName = GetCustomMetricName(enqueuedEventArgs.Data);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "enqueued")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("enqueued")).AnyContext();
        }

        protected override async void OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            base.OnDequeued(sender, dequeuedEventArgs);

            string customMetricName = GetCustomMetricName(dequeuedEventArgs.Data);
            if (!String.IsNullOrEmpty(customMetricName))
                dequeuedEventArgs.Metadata.Data[CustomMetricNameKey] = customMetricName;

            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "dequeued")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("dequeued")).AnyContext();

            var metadata = dequeuedEventArgs.Metadata;
            if (metadata == null || metadata.EnqueuedTimeUtc == DateTime.MinValue || metadata.DequeuedTimeUtc == DateTime.MinValue)
                return;

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            var time = (long)(end - start).TotalMilliseconds;

            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(customMetricName, "queuetime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("queuetime"), time).AnyContext();
        }

        protected override async void OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            base.OnCompleted(sender, completedEventArgs);

            string customMetricName = GetCustomMetricName(completedEventArgs.Metadata);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.CounterAsync(GetFullMetricName(customMetricName, "completed")).AnyContext();
            await _metricsClient.CounterAsync(GetFullMetricName("completed")).AnyContext();

            var time = (long)(completedEventArgs.Metadata?.ProcessingTime.TotalMilliseconds ?? 0D);
            if (!String.IsNullOrEmpty(customMetricName))
                await _metricsClient.TimerAsync(GetFullMetricName(customMetricName, "processtime"), time).AnyContext();
            await _metricsClient.TimerAsync(GetFullMetricName("processtime"), time).AnyContext();
        }

        protected override async void OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            base.OnAbandoned(sender, abandonedEventArgs);

            string customMetricName = GetCustomMetricName(abandonedEventArgs.Metadata);
            string counter = GetFullMetricName(customMetricName, "abandoned");
            await _metricsClient.CounterAsync(counter).AnyContext();

            string timer = GetFullMetricName(customMetricName, "abandontime");
            var time = (long)abandonedEventArgs.Metadata?.ProcessingTime.TotalMilliseconds;
            await _metricsClient.TimerAsync(timer, time).AnyContext();
        }

        protected string GetCustomMetricName(QueueEntryMetadata metadata) {
            return metadata.Data.GetValueOrDefault<string>(CustomMetricNameKey);
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