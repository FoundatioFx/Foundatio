using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>
/// Renews the leases of one processor's unsettled deliveries with a single timer. A delivery is tracked from the moment
/// it is received until it is settled, so messages waiting behind an earlier message of their group keep their lease
/// too. Deliveries settled within a renewal interval never touch the timer, and a failed renewal is retried quickly,
/// well before the lease runs out. When the transport reports the broker's own lease end in
/// <see cref="IncomingMessage.LeaseExpiresAt"/>, renewals and retries follow it instead of the requested lease.
/// </summary>
internal sealed class LeaseRenewer : IDisposable
{
    private static readonly TimeSpan OwnershipCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MinRenewTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRenewTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TimerResolution = TimeSpan.FromMilliseconds(5);

    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _interval;
    private readonly bool _leaseExpiration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly HashSet<MessageDelivery> _deliveries = [];
    private readonly ITimer _timer;
    private DateTimeOffset? _timerDueAt;
    private bool _disposed;

    public LeaseRenewer(TimeSpan leaseDuration, bool leaseExpiration, TimeProvider timeProvider, ILogger logger)
    {
        _leaseDuration = leaseDuration;
        _leaseExpiration = leaseExpiration;
        _timeProvider = timeProvider;
        _logger = logger;

        // A third of the way through the lease leaves two thirds to retry a renewal that failed.
        _interval = TimeSpan.FromTicks(Math.Max(1, leaseDuration.Ticks / 3));

        // Without lease expiration, renewing only asks whether the delivery is still ours (a revoked Kafka partition), so ask often.
        if (!leaseExpiration && _interval > OwnershipCheckInterval)
            _interval = OwnershipCheckInterval;

        // Renewals must not run in the context (activity, message context) of whoever created the processor.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _timer = CreateTimer();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
                _timer = CreateTimer();
        }
    }

    /// <summary>Starts renewing deliveries that were just received.</summary>
    public void Track(IReadOnlyList<MessageDelivery> deliveries)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            foreach (var delivery in deliveries)
                TrackLocked(delivery, now, renewNow: false);
        }
    }

    /// <summary>Starts renewing a delivery again; with <paramref name="renewNow"/>, the first renewal is due at once.</summary>
    public void Track(MessageDelivery delivery, bool renewNow)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
            TrackLocked(delivery, now, renewNow);
    }

    /// <summary>Stops renewing a delivery, cancelling and waiting for a renewal in flight.</summary>
    public async Task UntrackAsync(MessageDelivery delivery)
    {
        Task? renewal;
        CancellationTokenSource? cancellation;
        lock (_lock)
        {
            if (!delivery.Tracked)
                return;

            delivery.Tracked = false;
            _deliveries.Remove(delivery);
            renewal = delivery.Renewal;
            cancellation = delivery.RenewalCancellation;
        }

        if (renewal is null)
            return;

        TryCancel(cancellation);
        await renewal.AnyContext();
    }

    public void Dispose()
    {
        List<CancellationTokenSource> renewals = [];
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var delivery in _deliveries)
            {
                delivery.Tracked = false;
                if (delivery.RenewalCancellation is { } cancellation)
                    renewals.Add(cancellation);
            }

            _deliveries.Clear();
        }

        _timer.Dispose();
        foreach (var cancellation in renewals)
            TryCancel(cancellation);
    }

    private void TrackLocked(MessageDelivery delivery, DateTimeOffset now, bool renewNow)
    {
        if (_disposed || delivery.Tracked || delivery.IsLeaseLost)
            return;

        if (!renewNow)
            delivery.LeaseExpiresAt = delivery.Message.LeaseExpiresAt ?? now + _leaseDuration;

        delivery.RenewAt = renewNow ? now : now + GetRenewInterval(delivery, now);
        delivery.RenewalFailures = 0;
        delivery.Tracked = true;
        _deliveries.Add(delivery);
        ScheduleLocked(delivery.RenewAt, now);
    }

    private void OnTimer()
    {
        lock (_lock)
        {
            _timerDueAt = null;
            if (_disposed)
                return;

            var now = _timeProvider.GetUtcNow();
            List<MessageDelivery>? due = null;
            DateTimeOffset? next = null;
            foreach (var delivery in _deliveries)
            {
                if (delivery.Renewal is not null)
                    continue;

                if (delivery.RenewAt <= now + TimerResolution)
                    (due ??= []).Add(delivery);
                else if (next is null || delivery.RenewAt < next)
                    next = delivery.RenewAt;
            }

            if (due is not null)
            {
                foreach (var delivery in due)
                {
                    var cancellation = new CancellationTokenSource();
                    delivery.RenewalCancellation = cancellation;
                    delivery.Renewal = Task.Run(() => RenewAsync(delivery, cancellation), CancellationToken.None);
                }
            }

            if (next is { } at)
                ScheduleLocked(at, now);
        }
    }

    private async Task RenewAsync(MessageDelivery delivery, CancellationTokenSource cancellation)
    {
        var started = _timeProvider.GetUtcNow();
        cancellation.CancelAfter(Clamp(delivery.LeaseExpiresAt - started, MinRenewTimeout, MaxRenewTimeout));

        bool lost = false;
        Exception? error = null;
        try
        {
            await delivery.Receiver.RenewAsync(delivery.Message, cancellation.Token).AnyContext();
        }
        catch (MessageLeaseLostException)
        {
            lost = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        bool tracked;
        var retryIn = TimeSpan.Zero;
        lock (_lock)
        {
            delivery.Renewal = null;
            delivery.RenewalCancellation = null;
            tracked = delivery.Tracked;
            if (tracked)
            {
                var now = _timeProvider.GetUtcNow();
                if (lost)
                {
                    delivery.Tracked = false;
                    _deliveries.Remove(delivery);
                }
                else if (error is null)
                {
                    delivery.LeaseExpiresAt = delivery.Message.LeaseExpiresAt ?? started + _leaseDuration;
                    delivery.RenewalFailures = 0;
                    delivery.RenewAt = now + GetRenewInterval(delivery, now);
                    ScheduleLocked(delivery.RenewAt, now);
                }
                else
                {
                    retryIn = GetRetryDelay(++delivery.RenewalFailures, delivery.LeaseExpiresAt - now);
                    delivery.RenewAt = now + retryIn;
                    ScheduleLocked(delivery.RenewAt, now);
                }
            }
        }

        cancellation.Dispose();
        if (!tracked)
            return;

        if (lost)
            delivery.MarkLeaseLost();
        else if (error is not null)
            _logger.LogWarning(error, "Could not renew the lease on message {MessageId}; retrying in {Delay}", delivery.Message.MessageId, retryIn);
    }

    /// <summary>
    /// A third of the requested lease or, when the broker decides the lease, a third of what is left of it, never less
    /// than <see cref="FirstRetryDelay"/>. Without lease expiration, at most <see cref="OwnershipCheckInterval"/>.
    /// </summary>
    private TimeSpan GetRenewInterval(MessageDelivery delivery, DateTimeOffset now)
    {
        if (delivery.Message.LeaseExpiresAt is null)
            return _interval;

        var interval = TimeSpan.FromTicks((delivery.LeaseExpiresAt - now).Ticks / 3);
        if (!_leaseExpiration && interval > OwnershipCheckInterval)
            interval = OwnershipCheckInterval;

        return interval < FirstRetryDelay ? FirstRetryDelay : interval;
    }

    /// <summary>Backs off from <see cref="FirstRetryDelay"/>, at most to the renewal interval, and to half of what is left of the lease.</summary>
    private TimeSpan GetRetryDelay(int failures, TimeSpan remaining)
    {
        var delay = TimeSpan.FromTicks(Math.Min(FirstRetryDelay.Ticks * (1L << Math.Min(failures - 1, 20)), _interval.Ticks));
        if (remaining > TimeSpan.Zero && delay.Ticks > remaining.Ticks / 2)
            delay = TimeSpan.FromTicks(remaining.Ticks / 2);

        return delay < FirstRetryDelay ? FirstRetryDelay : delay;
    }

    private void ScheduleLocked(DateTimeOffset at, DateTimeOffset now)
    {
        if (_disposed || _timerDueAt is { } dueAt && dueAt <= at)
            return;

        _timerDueAt = at;
        var delay = at - now;

        // Never zero: a fake time provider fires a due timer synchronously, inside this lock.
        _timer.Change(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay, Timeout.InfiniteTimeSpan);
    }

    private ITimer CreateTimer() => _timeProvider.CreateTimer(static state => ((LeaseRenewer)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
