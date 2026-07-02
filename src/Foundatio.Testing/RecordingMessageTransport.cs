using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;

namespace Foundatio.Messaging.Testing;

/// <summary>
/// The harness transport: a fully-capable in-memory transport that records every send and settlement so tests can
/// assert on what actually moved through the bus, and tracks the destinations/sources it has seen so
/// <see cref="MessagingTestHarness.WaitForIdleAsync"/> can detect quiescence.
/// </summary>
internal sealed class RecordingMessageTransport : IMessageTransport, ISupportsPull, ISupportsPush, ISupportsVisibilityTimeout,
    ISupportsDeadLetter, ISupportsRedeliveryDelay, ISupportsLockRenewal, ISupportsStats, ISupportsPriority,
    ISupportsExpiration, ISupportsProvisioning, ITransportInfo
{
    // A delayed redelivery lives only in the inner transport's timer until it fires — neither queued nor in flight —
    // so idle detection would report quiescent while a retry is pending. Give the timer this long past its due time to
    // materialize the redelivered message back into stats before the pending marker is dropped.
    private static readonly TimeSpan _redeliveryGrace = TimeSpan.FromMilliseconds(250);

    private readonly InMemoryMessageTransport _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentQueue<RecordedMessage> _sent = new();
    private readonly ConcurrentQueue<RecordedMessage> _published = new();
    private readonly ConcurrentQueue<RecordedMessage> _handled = new();
    private readonly ConcurrentQueue<RecordedMessage> _abandoned = new();
    private readonly ConcurrentQueue<RecordedMessage> _deadLettered = new();
    private readonly ConcurrentDictionary<string, byte> _knownNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, (string Destination, DateTimeOffset DueAt)> _pendingRedeliveries = new();

    public RecordingMessageTransport(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inner = new InMemoryMessageTransport(timeProvider);
    }

    public IReadOnlyList<RecordedMessage> Sent => [.. _sent];
    public IReadOnlyList<RecordedMessage> Published => [.. _published];
    public IReadOnlyList<RecordedMessage> Handled => [.. _handled];
    public IReadOnlyList<RecordedMessage> Abandoned => [.. _abandoned];
    public IReadOnlyList<RecordedMessage> DeadLettered => [.. _deadLettered];

    public DeliveryGuarantee DeliveryGuarantee => _inner.DeliveryGuarantee;
    public OrderingGuarantee Ordering => _inner.Ordering;
    public IReadOnlySet<DestinationRole> SupportedRoles => _inner.SupportedRoles;
    public int? MaxBatchSize => _inner.MaxBatchSize;
    public long? MaxMessageBytes => _inner.MaxMessageBytes;
    public TimeSpan? MaxVisibilityTimeout => _inner.MaxVisibilityTimeout;
    public TimeSpan? MaxRedeliveryDelay => _inner.MaxRedeliveryDelay;

    public async Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        var result = await _inner.SendAsync(destination, messages, options, ct).ConfigureAwait(false);

        _knownNames.TryAdd(destination, 0);
        var recordings = options.DestinationRole == DestinationRole.Topic ? _published : _sent;
        foreach (var message in messages)
        {
            recordings.Enqueue(new RecordedMessage
            {
                Destination = destination,
                Role = options.DestinationRole,
                MessageType = message.Headers.GetValueOrDefault(KnownHeaders.MessageType),
                Body = message.Body,
                Headers = message.Headers
            });
        }

        return result;
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct = default)
    {
        _knownNames.TryAdd(source, 0);
        return _inner.ReceiveAsync(source, request, ct);
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct = default)
    {
        _knownNames.TryAdd(source, 0);
        return _inner.ReceiveAsync(source, request, visibility, ct);
    }

    public Task<IPushSubscription> SubscribeAsync(string source, Func<TransportEntry, CancellationToken, Task> onMessage, PushOptions options, CancellationToken ct = default)
    {
        _knownNames.TryAdd(source, 0);
        return _inner.SubscribeAsync(source, onMessage, options, ct);
    }

    public async Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
    {
        await _inner.CompleteAsync(entry, ct).ConfigureAwait(false);
        _handled.Enqueue(Record(entry));
    }

    public async Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
    {
        await _inner.AbandonAsync(entry, ct).ConfigureAwait(false);
        _abandoned.Enqueue(Record(entry));
    }

    public async Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct = default)
    {
        Guid pendingToken = Guid.NewGuid();
        if (redeliveryDelay > TimeSpan.Zero)
            _pendingRedeliveries[pendingToken] = (entry.Destination, _timeProvider.GetUtcNow().Add(redeliveryDelay));

        try
        {
            await _inner.AbandonAsync(entry, redeliveryDelay, ct).ConfigureAwait(false);
        }
        catch
        {
            _pendingRedeliveries.TryRemove(pendingToken, out _);
            throw;
        }

        _abandoned.Enqueue(Record(entry));
    }

    public async Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct = default)
    {
        await _inner.DeadLetterAsync(entry, reason, ct).ConfigureAwait(false);
        _deadLettered.Enqueue(Record(entry) with { Reason = reason });
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveDeadLetteredAsync(string destination, ReceiveRequest request, CancellationToken ct = default)
        => _inner.ReceiveDeadLetteredAsync(destination, request, ct);

    public Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct = default)
        => _inner.RenewLockAsync(entry, duration, ct);

    public Task<MessageDestinationStats> GetStatsAsync(string destination, CancellationToken ct = default)
        => _inner.GetStatsAsync(destination, ct);

    public Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct = default)
    {
        foreach (var declaration in declarations)
            _knownNames.TryAdd(declaration.Name, 0);
        return _inner.EnsureAsync(declarations, ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct = default) => _inner.DeleteAsync(name, ct);

    public Task<bool> ExistsAsync(string name, CancellationToken ct = default) => _inner.ExistsAsync(name, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    // Aggregate pending work across every destination/source this transport has seen; idle means nothing queued,
    // nothing in flight, and no delayed redelivery still waiting on its timer.
    public async Task<IReadOnlyList<(string Name, long Queued, long Working)>> GetPendingAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var scheduled = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var redelivery in _pendingRedeliveries)
        {
            if (now >= redelivery.Value.DueAt + _redeliveryGrace)
                _pendingRedeliveries.TryRemove(redelivery.Key, out _);
            else
                scheduled[redelivery.Value.Destination] = scheduled.GetValueOrDefault(redelivery.Value.Destination) + 1;
        }

        var pending = new List<(string, long, long)>();
        foreach (string name in _knownNames.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var stats = await _inner.GetStatsAsync(name, ct).ConfigureAwait(false);
            long queued = stats.Queued + scheduled.GetValueOrDefault(name);
            if (queued > 0 || stats.Working > 0)
                pending.Add((name, queued, stats.Working));
        }

        return pending;
    }

    private static RecordedMessage Record(TransportEntry entry) => new()
    {
        Destination = entry.Destination,
        Role = DestinationRole.Queue,
        MessageType = entry.Headers.GetValueOrDefault(KnownHeaders.MessageType),
        Body = entry.Body,
        Headers = entry.Headers,
        Attempts = entry.DeliveryCount
    };
}
