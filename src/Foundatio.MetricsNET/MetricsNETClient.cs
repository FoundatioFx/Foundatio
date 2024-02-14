using System;
using Metrics;

namespace Foundatio.Metrics;

#pragma warning disable CS0618 // Type or member is obsolete
public class MetricsNETClient : IMetricsClient
#pragma warning restore CS0618 // Type or member is obsolete
{
    public void Counter(string name, int value = 1)
    {
        Metric.Counter(name, Unit.None).Increment();
    }

    public void Gauge(string name, double value)
    {
        Metric.Gauge(name, () => value, Unit.None);
    }

    public void Timer(string name, int milliseconds)
    {
        Metric.Timer(name, Unit.Calls, SamplingType.SlidingWindow, TimeUnit.Milliseconds).Record(milliseconds, TimeUnit.Milliseconds);
    }

    public void Dispose() { }
}
