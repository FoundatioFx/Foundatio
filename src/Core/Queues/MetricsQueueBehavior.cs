using Foundatio.Metrics;

namespace Foundatio.Queues {
    public class MetricsQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly IMetricsClient _metrics;

        public MetricsQueueBehavior(IMetricsClient metrics) {
            _metrics = metrics;
        }

        protected override void OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            _metrics.CounterAsync("dequeued");
        }
    }
}