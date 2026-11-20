using System;
using System.Threading;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>
/// Decides how many messages a processor that reads ahead (<see cref="TransportCapabilities.ReceiveAhead"/>) may have
/// received beyond its free handler slots: its depth. By Little's law, from moving averages of how long a message keeps
/// its handler slot (<c>s</c>) and how long a receive that returned everything it asked for took (<c>t</c>), the depth
/// is what the handlers get through during one receive plus one batch, <c>min(⌈C·t/s⌉, b·R) + b</c>, at most
/// <c>⌊C·H/s⌋</c> so that a buffered message waits about the horizon <c>H</c> at most. <c>H</c> is a second, or a
/// quarter of the shortest lease a receive was granted, counted from when it started, when that is less, so a buffered
/// message normally starts before its first renewal.
/// The depth is zero until both times have been measured, and while a message keeps its slot longer than <c>H</c>, so
/// slow handlers do not hoard messages.
/// </summary>
/// <remarks>Times are in timestamp ticks of the processor's <see cref="TimeProvider"/>. Thread-safe.</remarks>
internal sealed class ReceiveAhead
{
    /// <summary>Above this many bytes of buffered message bodies, the depth counts as zero.</summary>
    internal const long MaxBufferedBytes = 16 * 1024 * 1024;

    private const double SlotTimeWeight = 1.0 / 16;
    private const double ReceiveTimeWeight = 1.0 / 8;
    private static readonly TimeSpan MaxHorizon = TimeSpan.FromSeconds(1);

    private readonly int _maxConcurrency;
    private readonly int _batchSize;
    private readonly int _receives;
    private readonly long _timestampFrequency;
    private long _shortestLeaseTicks;
    private double _slotTicks = -1;
    private double _receiveTicks = -1;
    private int _depth;

    /// <param name="maxConcurrency">The handler slots, <c>C</c>.</param>
    /// <param name="batchSize">The most messages one receive asks for, <c>b</c>: at most <paramref name="maxConcurrency"/>.</param>
    /// <param name="receives">How many receives may be in flight at once, <c>R</c>.</param>
    /// <param name="leaseDuration">The lease the processor requests.</param>
    /// <param name="timestampFrequency">Ticks per second of the timestamps passed in.</param>
    public ReceiveAhead(int maxConcurrency, int batchSize, int receives, TimeSpan leaseDuration, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfLessThan(receives, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timestampFrequency, 1);

        _maxConcurrency = maxConcurrency;
        _batchSize = batchSize;
        _receives = receives;
        _timestampFrequency = timestampFrequency;
        _shortestLeaseTicks = leaseDuration.Ticks;
    }

    /// <summary>How many messages may be received beyond the free handler slots now, ignoring the byte cap.</summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <summary>The depth with <paramref name="bufferedBytes"/> of message bodies buffered: zero above <see cref="MaxBufferedBytes"/>.</summary>
    public int GetDepth(long bufferedBytes) => bufferedBytes > MaxBufferedBytes ? 0 : Depth;

    /// <summary>Records how long a message kept its handler slot. Returns true when the depth rose.</summary>
    public bool RecordSlotTime(long ticks) => Record(ref _slotTicks, ticks, SlotTimeWeight);

    /// <summary>Records how long a receive that returned every message it asked for took. Returns true when the depth rose.</summary>
    public bool RecordReceiveTime(long ticks) => Record(ref _receiveTicks, ticks, ReceiveTimeWeight);

    /// <summary>
    /// Records a lease the broker granted (from <see cref="IncomingMessage.LeaseExpiresAt"/>), counted from when its
    /// receive started, so a long poll's wait does not shorten it. The horizon follows the shortest one seen. Returns true
    /// when the depth rose.
    /// </summary>
    public bool RecordLease(TimeSpan lease)
    {
        long ticks = Math.Max(0, lease.Ticks), current;
        do
        {
            current = Interlocked.Read(ref _shortestLeaseTicks);
            if (ticks >= current)
                return false;
        } while (Interlocked.CompareExchange(ref _shortestLeaseTicks, ticks, current) != current);

        return Update();
    }

    private bool Record(ref double average, long ticks, double weight)
    {
        double sample = Math.Max(0, ticks);
        double current, next;
        do
        {
            current = Volatile.Read(ref average);
            next = current < 0 ? sample : current + (sample - current) * weight;
        } while (Interlocked.CompareExchange(ref average, next, current) != current);

        return Update();
    }

    /// <summary>
    /// Recalculates the depth. The depth is read before the averages, so a calculation from older averages never
    /// overwrites one from newer averages.
    /// </summary>
    private bool Update()
    {
        int previous, depth;
        do
        {
            previous = Volatile.Read(ref _depth);
            depth = Calculate();
        } while (depth != previous && Interlocked.CompareExchange(ref _depth, depth, previous) != previous);

        return depth > previous;
    }

    private int Calculate()
    {
        double slot = Volatile.Read(ref _slotTicks), receive = Volatile.Read(ref _receiveTicks);
        if (slot < 0 || receive < 0)
            return 0;

        // A timestamp may not move during a very short call; a tick stands in for no time at all.
        slot = Math.Max(1, slot);
        double horizon = GetHorizonTicks();
        if (slot > horizon)
            return 0;

        double usedDuringReceive = Math.Min(Math.Ceiling(_maxConcurrency * receive / slot), (double)_batchSize * _receives);
        double waitCap = Math.Floor(_maxConcurrency * horizon / slot);
        return (int)Math.Min(usedDuringReceive + _batchSize, waitCap);
    }

    /// <summary>A quarter of the shortest lease, at most <see cref="MaxHorizon"/>, in timestamp ticks.</summary>
    private double GetHorizonTicks()
    {
        long horizon = Math.Min(Interlocked.Read(ref _shortestLeaseTicks) / 4, MaxHorizon.Ticks);
        return (double)horizon / TimeSpan.TicksPerSecond * _timestampFrequency;
    }
}
