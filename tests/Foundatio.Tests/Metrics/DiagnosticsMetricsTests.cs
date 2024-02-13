using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics;

public class DiagnosticsMetricsTests : TestWithLoggingBase, IDisposable
{
    private readonly DiagnosticsMetricsClient _client;

    public DiagnosticsMetricsTests(ITestOutputHelper output) : base(output)
    {
        Log.Options.DefaultMinimumLevel = LogLevel.Trace;
        _client = new DiagnosticsMetricsClient(o => o.MeterName("Test"));
    }

    [Fact]
    public void Counter()
    {
        using var metricsCollector = new DiagnosticsMetricsCollector("Test", _logger);

        _client.Counter("counter");

        Assert.Single(metricsCollector.GetMeasurements<int>());
        Assert.Equal("counter", metricsCollector.GetMeasurements<int>().Single().Name);
        Assert.Equal(1, metricsCollector.GetMeasurements<int>().Single().Value);
    }

    [Fact]
    public void CounterWithValue()
    {
        using var metricsCollector = new DiagnosticsMetricsCollector("Test", _logger);

        _client.Counter("counter", 5);
        _client.Counter("counter", 3);

        Assert.Equal(2, metricsCollector.GetMeasurements<int>().Count);
        Assert.All(metricsCollector.GetMeasurements<int>(), m =>
        {
            Assert.Equal("counter", m.Name);
        });
        Assert.Equal(8, metricsCollector.GetSum<int>("counter"));
        Assert.Equal(2, metricsCollector.GetCount<int>("counter"));
    }

    [Fact]
    public void Gauge()
    {
        using var metricsCollector = new DiagnosticsMetricsCollector("Test", _logger);

        _client.Gauge("gauge", 1.1);

        metricsCollector.RecordObservableInstruments();

        Assert.Single(metricsCollector.GetMeasurements<double>()); ;
        Assert.Equal("gauge", metricsCollector.GetMeasurements<double>().Single().Name);
        Assert.Equal(1.1, metricsCollector.GetMeasurements<double>().Single().Value);
    }

    [Fact]
    public void Timer()
    {
        using var metricsCollector = new DiagnosticsMetricsCollector("Test", _logger);

        _client.Timer("timer", 450);
        _client.Timer("timer", 220);

        Assert.Equal(670, metricsCollector.GetSum<double>("timer"));
        Assert.Equal(2, metricsCollector.GetCount<double>("timer"));
    }

    [Fact]
    public async Task CanWaitForCounter()
    {
        using var metricsCollector = new DiagnosticsMetricsCollector("Test", _logger);

        var success = await metricsCollector.WaitForCounterAsync<int>("timer", () =>
        {
            _client.Counter("timer", 1);
            _client.Counter("timer", 2);
            return Task.CompletedTask;
        }, 3);

        Assert.True(success);
    }

    [Fact]
    public async Task CanTimeoutWaitingForCounter()
    {
        using var metricsCollector = new DiagnosticsMetricsCollector("Test", _logger);

        var success = await metricsCollector.WaitForCounterAsync<int>("timer", () =>
        {
            _client.Counter("timer", 1);
            _client.Counter("timer", 2);
            return Task.CompletedTask;
        }, 4, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);

        Assert.False(success);
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
