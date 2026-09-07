using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>Coordinates automatic and explicit renewal against one conservative delivery deadline.</summary>
internal sealed class MessageDeliveryLease : IAsyncDisposable
{
    private readonly IMessageTransport _transport;
    private readonly TransportEntry _entry;
    private readonly TimeSpan _duration;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _processing;
    private readonly CancellationTokenSource _renewal = new();
    private readonly SemaphoreSlim _gate = new(1);
    private long _expiresTicks;
    private int _lost;
    private int _settled;

    public MessageDeliveryLease(IMessageTransport transport, TransportEntry entry, TimeSpan duration, bool autoRenew,
        TimeProvider time, ILogger logger, CancellationTokenSource processing)
    {
        _transport = transport;
        _entry = entry;
        _duration = transport is ISupportsVisibilityTimeout { MaxVisibilityTimeout: { } maximum } && duration > maximum ? maximum : duration;
        _time = time;
        _logger = logger;
        _processing = processing;
        _expiresTicks = entry.LockExpiresUtc?.UtcTicks ?? DateTimeOffset.MaxValue.UtcTicks;
        Completion = entry.LockExpiresUtc is null ? Task.CompletedTask : MonitorAsync(autoRenew);
    }

    public Task Completion { get; }
    public bool IsLost => Volatile.Read(ref _lost) != 0 || !IsSettled && Remaining <= TimeSpan.Zero;
    public bool IsSettled => Volatile.Read(ref _settled) != 0;
    private TimeSpan Remaining => new(Interlocked.Read(ref _expiresTicks) - _time.GetUtcNow().UtcTicks);

    public void Settled()
    {
        Interlocked.Exchange(ref _settled, 1);
        _renewal.Cancel();
    }

    public async Task RenewAsync(TimeSpan? duration, CancellationToken cancellationToken)
    {
        var extension = duration ?? _duration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(extension, TimeSpan.Zero);
        if (_transport is ISupportsVisibilityTimeout { MaxVisibilityTimeout: { } maximum } && extension > maximum)
            throw new ArgumentOutOfRangeException(nameof(duration), $"The transport supports a maximum lease of {maximum}.");
        if (_transport is not ISupportsLockRenewal renewal)
            throw new NotSupportedException($"Transport {_transport.GetType().Name} does not support delivery lease renewal.");
        await _gate.WaitAsync(cancellationToken).AnyContext();
        try
        {
            if (IsSettled || IsLost || Remaining <= TimeSpan.Zero)
                throw new ReceiptExpiredException();
            var remaining = Remaining;
            var started = _time.GetUtcNow();
            using var deadline = new CancellationTokenSource(remaining > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : remaining, _time);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            await renewal.RenewLockAsync(_entry, extension, operation.Token).WaitAsync(operation.Token).AnyContext();
            Interlocked.Exchange(ref _expiresTicks, started.Add(extension).UtcTicks);
        }
        finally { _gate.Release(); }
    }

    private async Task MonitorAsync(bool autoRenew)
    {
        var token = _renewal.Token;
        bool retry = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = Remaining;
                if (remaining <= TimeSpan.Zero) break;
                bool canRenew = autoRenew && _transport is ISupportsLockRenewal;
                var delay = !canRenew ? remaining : retry
                    ? TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, remaining.Ticks / 4)) : remaining / 2;
                await Task.Delay(delay, _time, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (token.IsCancellationRequested) return;
                if (Remaining <= TimeSpan.Zero) break;
                if (!canRenew) continue;
                try
                {
                    await RenewAsync(_duration, token).AnyContext();
                    retry = false;
                }
                catch (ReceiptExpiredException) { break; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    retry = true;
                    _logger.LogWarning(exception, "Unable to renew delivery {MessageId} at {Source}; retrying within its lease", _entry.Id, _entry.Destination);
                }
            }
            if (!token.IsCancellationRequested && !IsSettled)
            {
                Interlocked.Exchange(ref _lost, 1);
                _logger.LogWarning("Delivery lease lost for {MessageId} at {Source}; cancelling processing", _entry.Id, _entry.Destination);
                await _processing.CancelAsync().AnyContext();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _renewal.CancelAsync().AnyContext();
        await Completion.AnyContext();
        _renewal.Dispose();
        // Explicit renewal may still be unwinding after settlement. SemaphoreSlim owns no wait handle here.
    }
}
