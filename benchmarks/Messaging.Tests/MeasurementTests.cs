using Foundatio.Messaging.Benchmarks;
using Xunit;

namespace Foundatio.Messaging.Benchmarks.Tests;

public class MeasurementTests
{
    [Fact]
    public void Histogram_KnownDistribution_RetainsTailAndMaximum()
    {
        var histogram = new LatencyHistogram();
        for (int i = 1; i <= 100; i++) histogram.RecordMicroseconds(i * 1000);
        var result = histogram.Snapshot();
        Assert.Equal(100, result.Count);
        Assert.InRange(result.P50Milliseconds, 50, 51);
        Assert.InRange(result.P99Milliseconds, 99, 101);
        Assert.Equal(100, result.MaxMilliseconds);
    }

    [Fact]
    public async Task Tracker_DuplicateSubscriber_CannotHideMissingFanout()
    {
        using var tracker = new DeliveryTracker("run", 10, 2, 4, "body");
        await tracker.ReserveAsync(1, CancellationToken.None);
        tracker.Expect(0);
        var message = new LoadMessage("run", 0, System.Diagnostics.Stopwatch.GetTimestamp(), "body");
        tracker.Record(0, message);
        tracker.Record(0, message);
        Assert.Equal(1, tracker.UniqueDeliveries);
        Assert.Equal(1, tracker.Duplicates);
        Assert.Equal(1, tracker.OutstandingInputs);
        tracker.Record(1, message);
        Assert.Equal(2, tracker.UniqueDeliveries);
        Assert.Equal(0, tracker.OutstandingInputs);
    }

    [Fact]
    public async Task Tracker_InvalidPayloadOrRun_IsNotSuccessfulDelivery()
    {
        using var tracker = new DeliveryTracker("run", 10, 1, 4, "body");
        await tracker.ReserveAsync(1, CancellationToken.None);
        tracker.Expect(0);
        tracker.Record(0, new("other", 0, 1, "body"));
        tracker.Record(0, new("run", 0, 1, "wrong"));
        tracker.Record(0, new("run", 9, 1, "body"));
        Assert.Equal(3, tracker.InvalidDeliveries);
        Assert.Equal(0, tracker.UniqueDeliveries);
        Assert.Contains("run=other, expectedRun=run", tracker.FirstInvalid);
    }

    [Fact]
    public async Task Tracker_ConcurrentFanout_AccountsForEveryDelivery()
    {
        using var tracker = new DeliveryTracker("run", 100, 4, 100, "body");
        await tracker.ReserveAsync(100, CancellationToken.None);
        for (int i = 0; i < 100; i++) tracker.Expect(i);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(group => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) tracker.Record(group, new("run", i, 1, "body"));
        })));
        Assert.Equal(400, tracker.UniqueDeliveries);
        Assert.Equal(0, tracker.OutstandingInputs);
        Assert.Equal(0, tracker.Duplicates);
    }

    [Fact]
    public void Options_BatchExceedsOutstandingWindow_RejectsDeadlockRisk()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkOptions { ProducerConcurrency = 8, BatchSize = 10, MaxOutstanding = 32 }.Validate());
        Assert.Throws<ArgumentException>(() => new BenchmarkOptions { Transport = "redis", Engine = "masstransit" }.Validate());
    }
}
