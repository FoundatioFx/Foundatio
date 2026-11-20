using System;
using System.Threading;

namespace Foundatio.Messaging;

/// <summary>
/// Lets one processor's settlements run without their handler slots: once a handler has returned, a complete, abandon
/// or dead-letter that waits on the broker gives the delivery's slot to the next message, so a slot does not wait out a
/// round trip. The delivery stays in flight, and its outcome is reported, only once the settlement finishes. At most
/// <see cref="SettlementsPerSlot"/> settlements per handler slot run without their slots at once; past that a settlement
/// keeps its slot, so a slow broker cannot make settlements pile up without bound.
/// </summary>
internal sealed class SettlementSlots
{
    /// <summary>
    /// How many settlements may run without their slots for each handler slot. A settlement that waits for another call
    /// in flight, as batched SQS deletes do, takes two round trips, while a slot takes about one to receive its next message.
    /// </summary>
    internal const int SettlementsPerSlot = 2;

    private readonly long _capacity;
    private readonly Action<int> _releaseSlots;
    private int _count;

    /// <param name="maxConcurrency">The processor's handler slots.</param>
    /// <param name="releaseSlots">Gives the given number of handler slots back to the processor.</param>
    public SettlementSlots(int maxConcurrency, Action<int> releaseSlots)
    {
        _capacity = (long)maxConcurrency * SettlementsPerSlot;
        _releaseSlots = releaseSlots;
    }

    /// <summary>
    /// Called while <paramref name="delivery"/> waits to settle: gives its own handler slot to other messages if it still
    /// holds it and there is room.
    /// </summary>
    public void TryRelease(MessageDelivery delivery)
    {
        if (delivery.SequenceSlots <= 0 || delivery.SlotsReleased || delivery.SettlesOffSlot)
            return;

        int count;
        do
        {
            count = Volatile.Read(ref _count);
            if (count >= _capacity)
                return;
        } while (Interlocked.CompareExchange(ref _count, count + 1, count) != count);

        delivery.SettlesOffSlot = true;
        _releaseSlots(1);
    }

    /// <summary>A delivery whose settlement gave its slot away has left the processor.</summary>
    public void Leave() => Interlocked.Decrement(ref _count);
}
