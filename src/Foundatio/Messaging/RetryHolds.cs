using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;

namespace Foundatio.Messaging;

/// <summary>
/// Holds one processor's retries that the broker cannot delay (no <see cref="TransportCapabilities.MaxRetryDelay"/>,
/// or a delay under <see cref="TransportCapabilities.MinRetryDelay"/>) for their backoff, leased and renewed. A held
/// delivery gives its handler slot to other messages, along with the slots of the later messages of its group that
/// wait behind it in the batch, so a few failures do not stall the source. At most <see cref="HoldsPerSlot"/> deliveries
/// per handler slot are held without their slots at once; past that a hold keeps its slots, so a failure storm cannot
/// lease messages without bound. Stopping the processor ends every hold at once.
/// </summary>
internal sealed class RetryHolds
{
    /// <summary>
    /// How many deliveries may be held without their slots for each handler slot: a slot that handles a message every
    /// 10 ms, with a tenth of them failing once and held for 1 s, keeps ten held.
    /// </summary>
    internal const int HoldsPerSlot = 10;

    private readonly long _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _stopping;
    private readonly Action<int> _releaseSlots;
    private int _count;

    /// <param name="maxConcurrency">The processor's handler slots.</param>
    /// <param name="timeProvider">Times the holds.</param>
    /// <param name="stopping">Cancelled when the processor stops, which ends every hold.</param>
    /// <param name="releaseSlots">Gives the given number of handler slots back to the processor.</param>
    public RetryHolds(int maxConcurrency, TimeProvider timeProvider, CancellationToken stopping, Action<int> releaseSlots)
    {
        _capacity = (long)maxConcurrency * HoldsPerSlot;
        _timeProvider = timeProvider;
        _stopping = stopping;
        _releaseSlots = releaseSlots;
    }

    /// <summary>
    /// Holds <paramref name="delivery"/> for <paramref name="delay"/>, renewing its lease, and gives its sequence's
    /// slots to other messages when there is room. Ends early when the lease is lost, when <paramref name="abortToken"/>
    /// is cancelled or when the processor stops; check <see cref="MessageDelivery.IsLeaseLost"/> before settling.
    /// </summary>
    public async Task HoldAsync(MessageDelivery delivery, TimeSpan delay, CancellationToken abortToken)
    {
        delivery.ResumeRenewing();
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(abortToken, _stopping);
        delivery.AttachHandler(hold);
        TryReleaseSlots(delivery);
        await _timeProvider.SafeDelay(delay, hold.Token).AnyContext();
    }

    /// <summary>A delivery whose slot a hold gave away has left the processor.</summary>
    public void Leave() => Interlocked.Decrement(ref _count);

    private void TryReleaseSlots(MessageDelivery delivery)
    {
        int slots = delivery.SequenceSlots;
        if (slots <= 0 || delivery.SlotsReleased)
            return;

        int count;
        do
        {
            count = Volatile.Read(ref _count);
            if ((long)count + slots > _capacity)
                return;
        } while (Interlocked.CompareExchange(ref _count, count + slots, count) != count);

        delivery.SlotsReleased = true;
        _releaseSlots(slots);
    }
}
