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
    private readonly object _monitorGate = new();
    private readonly ITimer? _firstCheck;
    private readonly bool _autoRenew;
    private CancellationTokenSource? _renewal;
    private Task _completion = Task.CompletedTask;
    private Task _stopping = Task.CompletedTask;
    private bool _stopped;
    private SemaphoreSlim? _gate;
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
        _autoRenew = autoRenew;
        if (entry.LockExpiresUtc is not null)
        {
            // Most deliveries settle before their first lease check. A timer keeps supervision active
            // even for a blocking handler, without starting an async loop for every short delivery.
            _firstCheck = time.CreateTimer(static state => ((MessageDeliveryLease)state!).StartMonitor(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var delay = autoRenew && transport is ISupportsLockRenewal ? Remaining / 2 : Remaining;
            _firstCheck.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    public Task Completion { get { lock (_monitorGate) return _completion; } }
    public bool IsLost => Volatile.Read(ref _lost) != 0 || !IsSettled && Remaining <= TimeSpan.Zero;
    public bool IsSettled => Volatile.Read(ref _settled) != 0;
    private TimeSpan Remaining => new(Interlocked.Read(ref _expiresTicks) - _time.GetUtcNow().UtcTicks);

    public void Settled()
    {
        Interlocked.Exchange(ref _settled, 1);
        StopMonitoring();
    }

    private void StopMonitoring()
    {
        lock (_monitorGate)
        {
            if (_stopped) return;
            _stopped = true;
            _firstCheck?.Dispose();
            _stopping = _renewal?.CancelAsync() ?? Task.CompletedTask;
        }
    }

    private void StartMonitor()
    {
        TaskCompletionSource completion;
        CancellationToken token;
        lock (_monitorGate)
        {
            if (_stopped) return;
            _renewal = new CancellationTokenSource();
            token = _renewal.Token;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion.Task;
        }
        _ = RunMonitorAsync(completion, token);
    }

    private async Task RunMonitorAsync(TaskCompletionSource completion, CancellationToken token)
    {
        try
        {
            await MonitorAsync(token).AnyContext();
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    public async Task RenewAsync(TimeSpan? duration, CancellationToken cancellationToken)
    {
        var extension = duration ?? _duration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(extension, TimeSpan.Zero);
        if (_transport is ISupportsVisibilityTimeout { MaxVisibilityTimeout: { } maximum } && extension > maximum)
            throw new ArgumentOutOfRangeException(nameof(duration), $"The transport supports a maximum lease of {maximum}.");
        if (_transport is not ISupportsLockRenewal renewal)
            throw new NotSupportedException($"Transport {_transport.GetType().Name} does not support delivery lease renewal.");
        var gate = LazyInitializer.EnsureInitialized(ref _gate, static () => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken).AnyContext();
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
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        bool retry = false;
        bool firstCheck = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = Remaining;
                if (remaining <= TimeSpan.Zero) break;
                bool canRenew = _autoRenew && _transport is ISupportsLockRenewal;
                var delay = !canRenew ? remaining : retry
                    ? TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, remaining.Ticks / 4)) : remaining / 2;
                if (!firstCheck)
                    await Task.Delay(delay, _time, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                firstCheck = false;
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
        StopMonitoring();
        await _stopping.AnyContext();
        await Completion.AnyContext();
        _renewal?.Dispose();
        // Explicit renewal may still be unwinding after settlement. SemaphoreSlim owns no wait handle here.
    }
}
