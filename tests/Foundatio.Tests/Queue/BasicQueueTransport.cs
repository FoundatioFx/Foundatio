using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;

namespace Foundatio.Tests.Queue;

/// <summary>
/// A deliberately minimal transport for tests: basic competing-consumer pull semantics only — no native redelivery
/// delay, lock renewal, visibility timeout, delayed delivery, priority, or expiration. Headers are treated as opaque
/// (preserved but never interpreted) and the delivery count is owned solely by the transport, so it never seeds the
/// count from the <c>message.attempts</c> header. This models a real provider that lacks time-based capabilities,
/// which exercises the runtime-store fallbacks and proves the core reconciles the attempt count itself rather than
/// relying on the transport to honor the header.
/// </summary>
internal sealed class BasicQueueTransport : IMessageTransport, ISupportsPull, ISupportsDeadLetter, ISupportsStats
{
    private readonly ConcurrentDictionary<string, Destination> _destinations = new(StringComparer.OrdinalIgnoreCase);

    public Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        var dest = _destinations.GetOrAdd(destination, static _ => new Destination());
        var results = new SendItemResult[messages.Count];
        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            string id = message.MessageId ?? options.DeduplicationId ?? Guid.NewGuid().ToString("N");
            dest.Ready.Enqueue(new StoredEntry(id, message.Body, message.Headers, DeliveryCount: 1));
            Interlocked.Increment(ref dest.Enqueued);
            results[index] = new SendItemResult { MessageId = id, Success = true };
        }

        return Task.FromResult(new SendResult { Items = results });
    }

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
    {
        var dest = _destinations.GetOrAdd(source, static _ => new Destination());
        int max = request.MaxMessages <= 0 ? 1 : request.MaxMessages;
        DateTimeOffset? deadline = request.MaxWaitTime is { } wait && wait > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(wait) : null;
        var entries = new List<TransportEntry>(max);

        while (true)
        {
            while (entries.Count < max && dest.Ready.TryDequeue(out var stored))
            {
                string token = Guid.NewGuid().ToString("N");
                dest.InFlight[token] = stored;
                Interlocked.Increment(ref dest.Dequeued);
                entries.Add(new TransportEntry
                {
                    Id = stored.Id,
                    Destination = source,
                    Body = stored.Body,
                    Headers = stored.Headers,
                    DeliveryCount = stored.DeliveryCount,
                    Receipt = new Receipt { TransportState = new BasicReceipt(source, token) }
                });
            }

            if (entries.Count > 0 || deadline is null || DateTimeOffset.UtcNow >= deadline)
                return entries;

            await Task.Delay(TimeSpan.FromMilliseconds(15), ct).ConfigureAwait(false);
        }
    }

    public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
    {
        var (dest, token) = Locate(entry);
        if (!dest.InFlight.TryRemove(token, out _))
            throw new ReceiptExpiredException();

        Interlocked.Increment(ref dest.Completed);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
    {
        var (dest, token) = Locate(entry);
        if (!dest.InFlight.TryRemove(token, out var stored))
            throw new ReceiptExpiredException();

        Interlocked.Increment(ref dest.Abandoned);
        dest.Ready.Enqueue(stored with { DeliveryCount = stored.DeliveryCount + 1 });
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct)
    {
        var (dest, token) = Locate(entry);
        if (!dest.InFlight.TryRemove(token, out var stored))
            throw new ReceiptExpiredException();

        var headers = String.IsNullOrEmpty(reason) ? stored.Headers : stored.Headers.ToBuilder().Set(KnownHeaders.DeadLetterReason, reason).Build();
        dest.Dead.Enqueue(stored with { Headers = headers });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveDeadLetteredAsync(string destination, ReceiveRequest request, CancellationToken ct)
    {
        var entries = new List<TransportEntry>();
        if (_destinations.TryGetValue(destination, out var dest))
        {
            int max = request.MaxMessages <= 0 ? 1 : request.MaxMessages;
            while (entries.Count < max && dest.Dead.TryDequeue(out var stored))
            {
                entries.Add(new TransportEntry
                {
                    Id = stored.Id,
                    Destination = destination,
                    Body = stored.Body,
                    Headers = stored.Headers,
                    DeliveryCount = stored.DeliveryCount,
                    Receipt = new Receipt { TransportState = null }
                });
            }
        }

        return Task.FromResult<IReadOnlyList<TransportEntry>>(entries);
    }

    public Task<MessageDestinationStats> GetStatsAsync(string destination, CancellationToken ct)
    {
        if (!_destinations.TryGetValue(destination, out var dest))
            return Task.FromResult(new MessageDestinationStats());

        return Task.FromResult(new MessageDestinationStats
        {
            Queued = dest.Ready.Count,
            Working = dest.InFlight.Count,
            Deadletter = dest.Dead.Count,
            Enqueued = Interlocked.Read(ref dest.Enqueued),
            Dequeued = Interlocked.Read(ref dest.Dequeued),
            Completed = Interlocked.Read(ref dest.Completed),
            Abandoned = Interlocked.Read(ref dest.Abandoned)
        });
    }

    public ValueTask DisposeAsync()
    {
        _destinations.Clear();
        return ValueTask.CompletedTask;
    }

    private (Destination Destination, string Token) Locate(TransportEntry entry)
    {
        if (entry.Receipt.TransportState is not BasicReceipt receipt || !_destinations.TryGetValue(receipt.Destination, out var dest))
            throw new ReceiptExpiredException();

        return (dest, receipt.Token);
    }

    private sealed record StoredEntry(string Id, ReadOnlyMemory<byte> Body, MessageHeaders Headers, int DeliveryCount);

    private sealed record BasicReceipt(string Destination, string Token);

    private sealed class Destination
    {
        public readonly ConcurrentQueue<StoredEntry> Ready = new();
        public readonly ConcurrentQueue<StoredEntry> Dead = new();
        public readonly ConcurrentDictionary<string, StoredEntry> InFlight = new(StringComparer.Ordinal);
        public long Enqueued;
        public long Dequeued;
        public long Completed;
        public long Abandoned;
    }
}
