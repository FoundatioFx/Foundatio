using System;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>The depth a processor reads ahead to, from the slot and receive times it measured.</summary>
public class ReceiveAheadTests
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(1);

    [Fact]
    public void Depth_WithFastHandlersOnASlowBroker_CoversOneReceivePlusABatch()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);

        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));

        // The handlers get through 16 x 20 / 50 = 6.4 messages during one receive, so 7, plus a batch.
        Assert.Equal(17, ahead.Depth);
    }

    [Fact]
    public void Depth_WithReceivesSlowerThanTheHandlers_IsCappedAtWhatTheReceivesCanCarryPlusABatch()
    {
        var ahead = Create(maxConcurrency: 64, batchSize: 10, receives: 8);

        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(1));

        Assert.Equal(10 * 8 + 10, ahead.Depth);
    }

    [Fact]
    public void Depth_WithHandlersSlowerThanTheHorizon_IsZero()
    {
        var ahead = Create(maxConcurrency: 4, batchSize: 4, receives: 2);

        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(2000));

        Assert.Equal(0, ahead.Depth);
    }

    [Fact]
    public void Depth_WithSlowHandlers_KeepsTheBufferedWaitWithinTheHorizon()
    {
        var ahead = Create(maxConcurrency: 2, batchSize: 2, receives: 2);

        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(900));

        // A receive's worth plus a batch would be 3, but 2 handlers get through only 2 x 1s / 900ms = 2.2 in a second.
        Assert.Equal(2, ahead.Depth);
    }

    [Fact]
    public void Depth_WithAShortLease_KeepsTheBufferedWaitWithinAQuarterOfIt()
    {
        var ahead = Create(maxConcurrency: 8, batchSize: 8, receives: 2, lease: TimeSpan.FromSeconds(2));

        ahead.RecordReceiveTime(Ticks(100));
        ahead.RecordSlotTime(Ticks(100));

        Assert.Equal(16, ahead.Depth);

        // The slot time moves to 600ms, longer than a quarter of the lease.
        ahead.RecordSlotTime(Ticks(100 + 16 * (600 - 100)));
        Assert.Equal(0, ahead.Depth);
    }

    [Fact]
    public void RecordLease_ShorterThanRequested_ShortensTheHorizon()
    {
        var ahead = Create(maxConcurrency: 4, batchSize: 4, receives: 2);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(400));
        Assert.Equal(5, ahead.Depth);

        Assert.False(ahead.RecordLease(TimeSpan.FromSeconds(1)));

        // A quarter of a second: handlers that keep a slot for 400ms no longer read ahead.
        Assert.Equal(0, ahead.Depth);
        Assert.False(ahead.RecordLease(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, ahead.Depth);
    }

    [Fact]
    public void Depth_BeforeAnyMeasurement_IsZero()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        Assert.Equal(0, ahead.Depth);

        Assert.False(ahead.RecordReceiveTime(Ticks(20)));
        Assert.Equal(0, ahead.Depth);

        Assert.True(ahead.RecordSlotTime(Ticks(50)));
        Assert.Equal(17, ahead.Depth);

        var slotFirst = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        Assert.False(slotFirst.RecordSlotTime(Ticks(50)));
        Assert.Equal(0, slotFirst.Depth);
    }

    [Fact]
    public void Depth_WhenTimesDoNotMove_StillReadsAheadABatch()
    {
        var ahead = Create(maxConcurrency: 1, batchSize: 1, receives: 1);

        ahead.RecordReceiveTime(0);
        ahead.RecordSlotTime(0);

        Assert.Equal(1, ahead.Depth);
    }

    [Fact]
    public void RecordSlotTime_AfterTheFirst_MovesTheAverageASixteenthOfTheWay()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));

        ahead.RecordSlotTime(Ticks(50 + 16 * 30));

        // The slot time is now 80ms: 16 x 20 / 80 = 4, plus a batch.
        Assert.Equal(14, ahead.Depth);
    }

    [Fact]
    public void GetDepth_WithBufferedBytesOverTheCap_IsZero()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));

        Assert.Equal(17, ahead.GetDepth(ReceiveAhead.MaxBufferedBytes));
        Assert.Equal(0, ahead.GetDepth(ReceiveAhead.MaxBufferedBytes + 1));
        Assert.Equal(17, ahead.Depth);
    }

    private static ReceiveAhead Create(int maxConcurrency, int batchSize, int receives, TimeSpan? lease = null)
        => new(maxConcurrency, batchSize, receives, lease ?? DefaultLease, TimeSpan.TicksPerSecond);

    private static long Ticks(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;
}
