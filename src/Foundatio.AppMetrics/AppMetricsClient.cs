using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Gauge;
using App.Metrics.Timer;

namespace Foundatio.Metrics;

#pragma warning disable CS0618 // Type or member is obsolete
public class AppMetricsClient : IMetricsClient
#pragma warning restore CS0618 // Type or member is obsolete
{
    private readonly IMetrics _metrics;

    public AppMetricsClient(IMetrics metrics)
    {
        _metrics = metrics;
    }

    public void Counter(string name, int value = 1)
    {
        _metrics.Provider.Counter.Instance(new CounterOptions { Name = name }).Increment(value);
    }

    public void Gauge(string name, double value)
    {
        _metrics.Provider.Gauge.Instance(new GaugeOptions { Name = name }).SetValue(value);
    }

    public void Timer(string name, int milliseconds)
    {
        _metrics.Provider.Timer.Instance(new TimerOptions { Name = name }).Record(milliseconds, TimeUnit.Milliseconds);
    }

    public void Dispose() { }
}
