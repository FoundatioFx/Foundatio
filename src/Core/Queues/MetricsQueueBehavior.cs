using System;
using Foundatio.Metrics;

namespace Foundatio.Queues
{
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class
    {
        private readonly string _metricsPrefix;
        private readonly IMetricsClient _metricsClient;
        private const string CustomMetricNameKey = "CustomMetricName";

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null)
        {
            _metricsClient = metrics;

            if (!string.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();
            _metricsPrefix = metricsPrefix;
        }

        protected override void OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs)
        {
            base.OnEnqueued(sender, enqueuedEventArgs);

            string customMetricName = GetCustomMetricName(enqueuedEventArgs.Data);
            if (!String.IsNullOrEmpty(customMetricName))
                _metricsClient.Counter(GetFullMetricName(customMetricName, "enqueued"));
            _metricsClient.Counter(GetFullMetricName("enqueued"));
        }

        protected override void OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs)
        {
            base.OnDequeued(sender, dequeuedEventArgs);

            string customMetricName = GetCustomMetricName(dequeuedEventArgs.Data);
            if (!String.IsNullOrEmpty(customMetricName))
                dequeuedEventArgs.Metadata.Data[CustomMetricNameKey] = customMetricName;

            if (!String.IsNullOrEmpty(customMetricName))
                _metricsClient.Counter(GetFullMetricName(customMetricName, "dequeued"));
            _metricsClient.Counter(GetFullMetricName("dequeued"));

            var metadata = dequeuedEventArgs.Metadata;
            if (metadata == null
                || metadata.EnqueuedTimeUtc == DateTime.MinValue
                || metadata.DequeuedTimeUtc == DateTime.MinValue)
                return;

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            var time = (long)(end - start).TotalMilliseconds;

            if (!String.IsNullOrEmpty(customMetricName))
                _metricsClient.Timer(GetFullMetricName(customMetricName, "queuetime"), time);
            _metricsClient.Timer(GetFullMetricName("queuetime"), time);
        }

        protected override void OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs)
        {
            base.OnCompleted(sender, completedEventArgs);

            string customMetricName = GetCustomMetricName(completedEventArgs.Metadata);
            if (!String.IsNullOrEmpty(customMetricName))
                _metricsClient.Counter(GetFullMetricName(customMetricName, "completed"));
            _metricsClient.Counter(GetFullMetricName("completed"));

            var time = (long)(completedEventArgs.Metadata?.ProcessingTime.TotalMilliseconds ?? 0D);
            if (!String.IsNullOrEmpty(customMetricName))
                _metricsClient.Timer(GetFullMetricName(customMetricName, "processtime"), time);
            _metricsClient.Timer(GetFullMetricName("processtime"), time);
        }

        protected override void OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs)
        {
            base.OnAbandoned(sender, abandonedEventArgs);

            string customMetricName = GetCustomMetricName(abandonedEventArgs.Metadata);
            string counter = GetFullMetricName(customMetricName, "abandoned");
            _metricsClient.Counter(counter);

            string timer = GetFullMetricName(customMetricName, "abandontime");
            var time = (long)abandonedEventArgs.Metadata?.ProcessingTime.TotalMilliseconds;
            _metricsClient.Timer(timer, time);
        }

        protected string GetCustomMetricName(QueueEntryMetadata metadata)
        {
            return metadata.Data.GetValueOrDefault<string>(CustomMetricNameKey);
        }

        protected string GetCustomMetricName(T data)
        {
            var haveStatName = data as IHaveMetricName;
            return haveStatName?.GetMetricName();
        }

        protected string GetFullMetricName(string name)
        {
            return string.Concat(_metricsPrefix, ".", name);
        }

        protected string GetFullMetricName(string customMetricName, string name)
        {
            return string.IsNullOrEmpty(customMetricName) 
                ? GetFullMetricName(name) 
                : string.Concat(_metricsPrefix, ".", customMetricName.ToLower(), ".", name);
        }
    }
}