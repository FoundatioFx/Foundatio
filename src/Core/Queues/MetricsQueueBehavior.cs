using System;
using Foundatio.Caching;
using Foundatio.Metrics;

namespace Foundatio.Queues
{
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class
    {
        private readonly InMemoryCacheClient _cache = new InMemoryCacheClient();

        public MetricsQueueBehavior(IMetricsClient metrics, string metricsPrefix = null)
        {
            MetricsClient = metrics;

            if (!string.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();
            MetricsPrefix = metricsPrefix;
        }

        public string MetricsPrefix { get; }

        public IMetricsClient MetricsClient { get; }


        protected override void OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs)
        {
            base.OnAbandoned(sender, abandonedEventArgs);

            var key = abandonedEventArgs.Metadata?.Id;
            var statName = GetStatName(key, null);

            string counter = FormatPrefix(statName, "abandoned");
            MetricsClient.Counter(counter);

            string timer = FormatPrefix(statName, "abandontime");
            var time = (long)(abandonedEventArgs.Metadata?.ProcessingTime.TotalMilliseconds ?? 0D);
            MetricsClient.Timer(timer, time);

            _cache.Remove(key);
        }

        protected override void OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs)
        {
            base.OnCompleted(sender, completedEventArgs);

            var key = completedEventArgs.Metadata?.Id;
            var statName = GetStatName(key, null);

            string counter = FormatPrefix(statName, "completed");
            MetricsClient.Counter(counter);

            string timer = FormatPrefix(statName, "processtime");
            var time = (long)(completedEventArgs.Metadata?.ProcessingTime.TotalMilliseconds ?? 0D);
            MetricsClient.Timer(timer, time);

            _cache.Remove(key);
        }

        protected override void OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs)
        {
            base.OnDequeued(sender, dequeuedEventArgs);

            var key = dequeuedEventArgs.Metadata?.Id;
            var statName = GetStatName(key, dequeuedEventArgs.Data as IMetricStatName);

            string counter = FormatPrefix(statName, "dequeued");
            MetricsClient.Counter(counter);

            var metadata = dequeuedEventArgs.Metadata;
            if (metadata == null)
                return;

            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            var time = (long)(end - start).TotalMilliseconds;

            string timer = FormatPrefix(statName, "queuetime");
            MetricsClient.Timer(timer, time);
        }

        protected override void OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs)
        {
            base.OnEnqueued(sender, enqueuedEventArgs);

            var key = enqueuedEventArgs.Id;
            var statName = GetStatName(key, enqueuedEventArgs.Data as IMetricStatName);

            string counter = FormatPrefix(statName, "enqueued");
            MetricsClient.Counter(counter);
        }


        protected string GetStatName(string key, IMetricStatName data)
        {
            var item = _cache.Get<string>(key);
            if (item != null)
                return item;

            var statName = data?.GetStatName()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(statName))
                return null;

            _cache.Add(key, statName, TimeSpan.FromHours(1));
            return statName;
        }

        protected string FormatPrefix(string stat)
        {
            return string.Concat(MetricsPrefix, ".", stat);
        }

        protected string FormatPrefix(string name, string stat)
        {
            return string.IsNullOrEmpty(name) 
                ? FormatPrefix(stat) 
                : string.Concat(MetricsPrefix, ".", name, ".", stat);
        }
    }
}