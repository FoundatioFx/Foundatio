using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

/// <summary>Provider-neutral queue statistics and bounded dead-letter inspection and recovery.</summary>
public sealed class MessageAdministration(IMessageTransport transport, TimeProvider? timeProvider = null, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private static DestinationAddress DeadLetters(DestinationAddress source) => DestinationAddress.ForQueue(source.Name + ".deadletter");

    public async Task<MessageDestinationStats> GetStatsAsync(DestinationAddress source, CancellationToken cancellationToken = default)
    {
        if (transport is not ISupportsStats stats) throw new NotSupportedException("This transport does not report queue statistics.");
        var value = await stats.GetStatsAsync(source, cancellationToken).ConfigureAwait(false);
        if (transport is ISupportsDeadLetterSink) return value;
        if (transport is ISupportsProvisioning provisioning && !await provisioning.ExistsAsync(DeadLetters(source), cancellationToken).ConfigureAwait(false)) return value;
        var dead = await stats.GetStatsAsync(DeadLetters(source), cancellationToken).ConfigureAwait(false);
        return value with { Deadletter = dead.Queued + dead.Working };
    }

    /// <summary>Returns a bounded snapshot. Inspection leaves the messages available for subsequent recovery.</summary>
    public async Task<IReadOnlyList<TransportEntry>> PeekDeadLettersAsync(DestinationAddress source, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (transport is ISupportsDeadLetter native)
            return await native.PeekDeadLetteredAsync(source, new DeadLetterQuery { Limit = limit }, cancellationToken).ConfigureAwait(false);
        var found = new List<TransportEntry>(limit);
        await VisitAsync(source, limit, (entry, _) => { found.Add(entry); return Task.FromResult(false); }, cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<bool> DeleteDeadLetterAsync(DestinationAddress source, string id, CancellationToken cancellationToken = default)
    {
        if (transport is ISupportsDeadLetter native) return await native.DeleteDeadLetteredAsync(source, id, cancellationToken).ConfigureAwait(false);
        bool deleted = false;
        await VisitAsync(source, 1000, async (entry, token) =>
        {
            if (entry.Id != id) return false;
            await transport.CompleteAsync(entry, token).ConfigureAwait(false);
            deleted = true;
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>Replays one message with optional new execution metadata. A confirmed send precedes deleting its dead letter.</summary>
    public async Task<bool> ReplayDeadLetterAsync(DestinationAddress source, string id,
        Func<TransportEntry, CancellationToken, Task<TransportMessage>>? prepare = null, CancellationToken cancellationToken = default)
    {
        if (transport is ISupportsDeadLetter native)
        {
            if (prepare is null) return await native.ReplayDeadLetteredAsync(source, id, source, cancellationToken).ConfigureAwait(false);
            string? after = null;
            for (int inspected = 0; inspected < 1000; inspected += 100)
            {
                var page = await native.PeekDeadLetteredAsync(source, new DeadLetterQuery { Limit = 100, AfterId = after }, cancellationToken).ConfigureAwait(false);
                if (page.FirstOrDefault(entry => entry.Id == id) is { } selected)
                {
                    await ReplayAsync(selected, cancellationToken).ConfigureAwait(false);
                    if (!await native.DeleteDeadLetteredAsync(source, id, cancellationToken).ConfigureAwait(false))
                        throw new MessageBusException("Replay was accepted, but the dead letter was already removed by another operation.");
                    return true;
                }
                if (page.Count < 100) return false;
                after = page[^1].Id;
            }
            return false;
        }
        bool replayed = false;
        await VisitAsync(source, 1000, async (entry, token) =>
        {
            if (entry.Id != id) return false;
            await ReplayAsync(entry, token).ConfigureAwait(false);
            await transport.CompleteAsync(entry, token).ConfigureAwait(false);
            replayed = true;
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return replayed;

        async Task ReplayAsync(TransportEntry entry, CancellationToken token)
        {
            var message = prepare is null
                ? new TransportMessage { Body = entry.Body, ContentType = entry.ContentType, Headers = MessageHeaders.Create(entry.Headers.Where(pair => pair.Key != KnownHeaders.Attempts && !pair.Key.StartsWith("message.dead_letter.", StringComparison.Ordinal)).ToDictionary()), MessageId = entry.ApplicationMessageId }
                : await prepare(entry, token).ConfigureAwait(false);
            var result = await transport.SendAsync(source, [message], new TransportSendOptions(), token).ConfigureAwait(false);
            if (result.Items.Count != 1 || result.Items[0].Status != MessageSendStatus.Accepted)
                throw new MessageBusException("Replay was not confirmed. The original dead letter was retained; an unknown send may still have been accepted.");
        }
    }

    // Providers without native peeking use bounded receive/hold/release. Hold non-matches for the whole scan so
    // a broker cannot repeatedly return the same first ten entries and hide a selected message further back.
    private async Task VisitAsync(DestinationAddress source, int limit, Func<TransportEntry, CancellationToken, Task<bool>> visit, CancellationToken cancellationToken)
    {
        if (transport is not ISupportsPull pull) throw new NotSupportedException("This transport cannot inspect dead letters.");
        var destination = DeadLetters(source);
        if (transport is ISupportsProvisioning provisioning && !await provisioning.ExistsAsync(destination, cancellationToken).ConfigureAwait(false)) return;
        var held = new List<TransportEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long started = Stopwatch.GetTimestamp();
        try
        {
            while (seen.Count < limit && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                var request = new ReceiveRequest { MaxMessages = Math.Min(10, limit - seen.Count), MaxWaitTime = TimeSpan.FromSeconds(1) };
                var batch = transport is ISupportsVisibilityTimeout visibility
                    ? await visibility.ReceiveAsync(destination, request, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)
                    : await pull.ReceiveAsync(destination, request, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0) return;
                held.AddRange(batch);
                foreach (var entry in batch)
                {
                    if (!seen.Add(entry.Id)) return;
                    if (await visit(entry, cancellationToken).ConfigureAwait(false)) { held.Remove(entry); return; }
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
            await Task.WhenAll(held.Select(async entry =>
            {
                try { await transport.AbandonAsync(entry, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (ReceiptExpiredException) { }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
                catch (Exception exception) { _logger.LogWarning(exception, "Unable to return inspected dead letter {MessageId}; its visibility lease will expire", entry.Id); }
            })).ConfigureAwait(false);
        }
    }
}
