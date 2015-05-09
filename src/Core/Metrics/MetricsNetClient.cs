namespace Foundatio.Metrics
{
    using System;
    using System.Threading.Tasks;

    using Foundatio.Utility;

    using global::Metrics;

    internal class MetricsNetClient : IMetricsClient
    {
        public void Dispose()
        {
        }

        public Task CounterAsync(string statName, int value = 1)
        {
            Metric.Counter(statName, Unit.None, "foundatio").Increment();
            return TaskHelper.Completed();
        }

        public Task GaugeAsync(string statName, double value)
        {
            Metric.Gauge(statName, () => value, Unit.None, "foundatio");
            return TaskHelper.Completed();
        }

        public Task TimerAsync(string statName, long milliseconds)
        {
            Metric.Timer(statName, Unit.Calls, SamplingType.SlidingWindow, TimeUnit.Milliseconds)
                .Record(milliseconds, TimeUnit.Milliseconds);
            return TaskHelper.Completed();
        }
    }
}