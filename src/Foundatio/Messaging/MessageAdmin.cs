using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

internal sealed class MessageAdmin : IMessageAdmin
{
    // Every receive waits long enough to be a long poll: a shorter wait is a short poll on SQS, which often comes back
    // empty while messages remain, and an empty receive ends a peek or replay.
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeekLease = TimeSpan.FromSeconds(30);

    // Headers that would stop a replayed message from being handled at once.
    private static readonly string[] ReplayRemovesHeaders = [KnownHeaders.DeadLetterReason, KnownHeaders.DeliverAt, KnownHeaders.ExpiresAt];

    private readonly MessagingRuntime _runtime;
    private readonly ILogger _logger;

    public MessageAdmin(MessagingRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<MessageAdmin>();
    }

    public Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.DeadLetter)
            throw new ArgumentException($"{source} has no counts of its own; read DeadLettered from the stats of {source.DeadLetterOf}.", nameof(source));

        return _runtime.Transport.GetStatsAsync(source, cancellationToken);
    }

    public async Task<IReadOnlyList<DeadLetterMessage>> PeekDeadLettersAsync(MessageSource source, int max = 20, CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        await using var receiver = await CreateDeadLetterReceiverAsync(source, cancellationToken).AnyContext();
        var received = new List<IncomingMessage>();
        try
        {
            while (received.Count < max)
            {
                var batch = await receiver.ReceiveAsync(Math.Min(max - received.Count, ReceiveBatchSize), ReceiveWait, cancellationToken).AnyContext();
                if (batch.Count == 0)
                    break;

                received.AddRange(batch);
            }

            return received.Select(ToDeadLetter).ToList();
        }
        finally
        {
            await ReleaseAsync(receiver, received).AnyContext();
        }
    }

    public async Task<int> ReplayDeadLettersAsync(MessageSource source, int max = 100, CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        var destination = MessageDestination.For(source);
        int batchSize = Math.Min(ReceiveBatchSize, Math.Max(1, _runtime.Transport.Capabilities.MaxSendBatchSize));
        int replayed = 0;

        // A replayed message that fails again is dead-lettered again while this call runs. Those are kept leased until
        // the end, so each dead letter is replayed at most once per call.
        var replayedIds = new HashSet<string>(StringComparer.Ordinal);
        var returned = new List<IncomingMessage>();

        await using var receiver = await CreateDeadLetterReceiverAsync(source, cancellationToken).AnyContext();
        try
        {
            while (replayed < max)
            {
                var batch = await receiver.ReceiveAsync(Math.Min(max - replayed, batchSize), ReceiveWait, cancellationToken).AnyContext();
                if (batch.Count == 0)
                    break;

                var fresh = new List<IncomingMessage>(batch.Count);
                foreach (var message in batch)
                {
                    if (replayedIds.Add(message.MessageId))
                        fresh.Add(message);
                    else
                        returned.Add(message);
                }

                if (fresh.Count == 0)
                    continue;

                await _runtime.Transport.SendAsync(destination, fresh.Select(m => new OutgoingMessage
                {
                    MessageId = m.MessageId,
                    Body = m.Body,
                    Headers = m.Headers.Where(h => !ReplayRemovesHeaders.Contains(h.Key, StringComparer.Ordinal)).ToDictionary(h => h.Key, h => h.Value),
                    GroupKey = m.GroupKey
                }).ToList(), cancellationToken).AnyContext();

                foreach (var message in fresh)
                    await receiver.CompleteAsync(message, CancellationToken.None).AnyContext();

                replayed += fresh.Count;
            }
        }
        finally
        {
            await ReleaseAsync(receiver, returned).AnyContext();
        }

        if (replayed > 0)
            _logger.LogInformation("Replayed {Count} dead letters to {Destination}", replayed, destination);

        return replayed;
    }

    public Task PurgeDeadLettersAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        return _runtime.Transport.DeleteAsync(MessageSource.DeadLetter(source), cancellationToken);
    }

    public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        return _runtime.Transport.DeleteAsync(source, cancellationToken);
    }

    private int ReceiveBatchSize => Math.Max(1, _runtime.Transport.Capabilities.MaxReceiveBatchSize);

    private async Task ReleaseAsync(IMessageReceiver receiver, IEnumerable<IncomingMessage> messages)
    {
        foreach (var message in messages)
        {
            try
            {
                await receiver.AbandonAsync(message, TimeSpan.Zero, CancellationToken.None).AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not release dead letter {MessageId}; the transport will release it", message.MessageId);
            }
        }
    }

    private Task<IMessageReceiver> CreateDeadLetterReceiverAsync(MessageSource source, CancellationToken cancellationToken)
    {
        return _runtime.Transport.CreateReceiverAsync(MessageSource.DeadLetter(source), new ReceiverOptions { LeaseDuration = PeekLease }, cancellationToken);
    }

    private DeadLetterMessage ToDeadLetter(IncomingMessage message)
    {
        string messageType = message.Headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty;
        return new DeadLetterMessage
        {
            MessageId = message.MessageId,
            MessageType = messageType,
            Reason = message.Headers.GetValueOrDefault(KnownHeaders.DeadLetterReason),
            CorrelationId = message.Headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            GroupKey = message.GroupKey,
            SentAt = message.Headers.TryGetValue(KnownHeaders.SentAt, out string? sentAt)
                && DateTimeOffset.TryParse(sentAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sent) ? sent : null,
            DeadLetteredAt = message.EnqueuedAt,
            Headers = message.Headers,
            Body = message.Body,
            Message = TryDeserialize(message, messageType)
        };
    }

    private object? TryDeserialize(IncomingMessage message, string messageType)
    {
        if (!_runtime.Types.TryGetType(message.Headers, out var type))
            return null;

        try
        {
            return _runtime.Serializer.Deserialize(message.Body, type);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not deserialize dead letter {MessageId} as {MessageType}", message.MessageId, messageType);
            return null;
        }
    }

    private static void ValidateSource(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind is not (MessageSourceKind.Queue or MessageSourceKind.Subscription))
            throw new ArgumentException($"Only queues and durable subscriptions have dead letters; {source} is a {source.Kind} source.", nameof(source));
    }
}
