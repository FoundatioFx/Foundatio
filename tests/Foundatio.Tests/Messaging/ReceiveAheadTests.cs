using System;
using System.Linq;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>The depth a processor reads ahead to, from the slot and receive times it measured.</summary>
public class ReceiveAheadTests
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(1);
    private const long MiB = 1024 * 1024;

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
    public void RecordReceiveTime_LongerThanTheHorizon_IsNotSampled()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));
        Assert.Equal(17, ahead.Depth);

        // A long poll of a drained source that a burst filled after six seconds measured the wait, not the broker.
        Assert.False(ahead.RecordReceiveTime(Ticks(6000)));
        Assert.Equal(17, ahead.Depth);

        // Within the horizon, a slower receive still counts: 20ms moves an eighth of the way to 180ms, 40ms.
        Assert.True(ahead.RecordReceiveTime(Ticks(180)));
        Assert.Equal(16 * 40 / 50 + 1 + 10, ahead.Depth);
    }

    [Fact]
    public void RecordReceiveTime_LongerThanTheHorizonBeforeAnyOther_CountsAsTheHorizon()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);

        ahead.RecordReceiveTime(Ticks(6000));
        ahead.RecordSlotTime(Ticks(50));

        // As if the receive took the horizon, a second: as many as the receives can carry, plus a batch.
        Assert.Equal(10 * 3 + 10, ahead.Depth);

        // Fast receives then bring it down from a second, not six: after 18, to 90ms, so 16 x 90 / 50 = 28.8, 29.
        for (int i = 0; i < 18; i++)
            ahead.RecordReceiveTime(0);
        Assert.Equal(29 + 10, ahead.Depth);
    }

    [Fact]
    public void GetDepth_WithBufferedBytesOverTheCap_IsZero()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));

        Assert.Equal(17, ahead.GetDepth(1, ReceiveAhead.MaxBufferedBytes));
        Assert.Equal(0, ahead.GetDepth(2, ReceiveAhead.MaxBufferedBytes + 1));
        Assert.Equal(17, ahead.Depth);
    }

    [Fact]
    public void GetDepth_WithLargeBodies_CoversOnlyTheBufferedAndAsManyMoreAsFitWithinTheCap()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));
        Assert.False(ahead.RecordBodies(10, 10 * 4 * MiB));

        // 16 MiB holds four 4 MiB bodies, buffered or still to be received.
        Assert.Equal(4, ahead.GetDepth(0, 0));
        Assert.Equal(4, ahead.GetDepth(1, 4 * MiB));
        Assert.Equal(3, ahead.GetDepth(2, 10 * MiB));
        Assert.Equal(17, ahead.Depth);

        // Bodies larger than the cap are never read ahead.
        ahead.RecordBodies(1, ReceiveAhead.MaxBufferedBytes + 1);
        Assert.Equal(0, ahead.GetDepth(0, 0));
    }

    [Fact]
    public void RecordBodies_WithLargerBodies_FollowsThemAtOnceAndSmallerOnesAnEighthOfTheWay()
    {
        var ahead = Create(maxConcurrency: 16, batchSize: 10, receives: 3);
        ahead.RecordReceiveTime(Ticks(20));
        ahead.RecordSlotTime(Ticks(50));
        ahead.RecordBodies(10, 10 * 1024);
        Assert.Equal(17, ahead.GetDepth(0, 0));

        Assert.False(ahead.RecordBodies(1, 8 * MiB));
        Assert.Equal(2, ahead.GetDepth(0, 0));

        // 8 MiB moves to 7, 6.1 and 5.4 MiB, so still two fit, then to 4.7 MiB, so three do.
        Assert.Equal([false, false, false, true], Enumerable.Range(0, 4).Select(_ => ahead.RecordBodies(1, 0)).ToArray());
        Assert.Equal(3, ahead.GetDepth(0, 0));
    }

    private static ReceiveAhead Create(int maxConcurrency, int batchSize, int receives, TimeSpan? lease = null)
        => new(maxConcurrency, batchSize, receives, lease ?? DefaultLease, TimeSpan.TicksPerSecond);

    private static long Ticks(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;
}
