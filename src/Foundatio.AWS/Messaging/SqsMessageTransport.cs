using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SnsNotFoundException = Amazon.SimpleNotificationService.Model.NotFoundException;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A message transport on Amazon SQS and SNS. A queue is an SQS queue; a topic is an SNS topic; a durable subscription
/// is an SQS queue subscribed to the topic with raw message delivery; an instance subscription is a per-receiver SQS
/// queue that is unsubscribed and deleted when the receiver is disposed, or by a later receiver once its heartbeat
/// tag goes stale. Every source has its own dead-letter queue. Queues the transport creates keep messages for 14 days.
/// Leases are visibility timeouts. Queues, topics, subscriptions and queue policies are created on first use. With
/// <see cref="SqsMessageTransportOptions.Fifo"/>, all of them are FIFO and group keys are ordered.
/// </summary>
/// <remarks>
/// <para>
/// SNS cannot delay a publish and FIFO queues cannot delay a single message, so such a message carries its delivery
/// time and is hidden with a visibility timeout when it is first received, again every 12 hours, until it is due.
/// Hidden messages count toward SQS's limit of in-flight messages per queue, so prefer delayed sends (or jobs) for
/// long or high-volume delays on standard queues.
/// </para>
/// <para>
/// Deleting a source keeps its queue, because SQS does not let a deleted queue's name be reused for 60 seconds and
/// purges are asynchronous. The subscription is removed from its topic, the visible messages are deleted, and the
/// queue is tagged with the queue's own time of the deletion so receivers discard messages that were in flight or
/// delayed and become visible later.
/// </para>
/// </remarks>
public sealed class SqsMessageTransport : IMessageTransport
{
    internal const int MaxBatchSize = 10;
    internal const int MaxVisibilityTimeoutSeconds = 12 * 60 * 60;
    internal const string HeartbeatTag = "foundatio-heartbeat";
    internal const string TopicTag = "foundatio-topic";
    internal const string PurgedBeforeTag = "foundatio-purged-before";
    internal const string SentTimestampAttribute = "SentTimestamp";
    internal const string ReceiveCountAttribute = "ApproximateReceiveCount";
    internal const string FirstReceiveTimestampAttribute = "ApproximateFirstReceiveTimestamp";
    private const int MaxBatchPayloadSize = 256 * 1024;
    private const int MaxSendAttempts = 3;
    private const int MaxInstanceChecksPerSweep = 20;
    private const int MaxSubscriptionPagesPerSweep = 10;
    private const int MaxSubscriptionPagesPerDelete = 100;
    private const int SweepIntervalInTimeouts = 6;
    private static readonly TimeSpan MaxNativeDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MessageRetention = TimeSpan.FromDays(14);

    /// <summary>Hidden messages are kept until due, but a day short of the retention period, so they can still be retried.</summary>
    private static readonly TimeSpan MaxEmulatedDelay = MessageRetention - TimeSpan.FromDays(1);

    private readonly bool _ownsClients;
    private readonly SqsMessageTransportNames _names;
    private readonly Dictionary<string, string> _queueAttributes;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _queueUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _topicArns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastSweeps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<SqsMessageReceiver, byte> _receivers = new();
    private int _disposed;

    /// <summary>Creates the SQS and SNS clients from <see cref="SqsMessageTransportOptions.ConnectionString"/> and disposes them with the transport.</summary>
    public SqsMessageTransport(SqsMessageTransportOptions options)
        : this(CreateSqsClient(options), CreateSnsClient(options), options, ownsClients: true)
    {
    }

    /// <summary>Uses the given clients, which the caller owns.</summary>
    public SqsMessageTransport(IAmazonSQS sqs, IAmazonSimpleNotificationService sns, SqsMessageTransportOptions? options = null)
        : this(sqs, sns, options, ownsClients: false)
    {
    }

    private SqsMessageTransport(IAmazonSQS sqs, IAmazonSimpleNotificationService sns, SqsMessageTransportOptions? options, bool ownsClients)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(sns);
        Options = options ?? new SqsMessageTransportOptions();
        SqsMessageTransportNames.ValidatePrefix(Options.NamePrefix, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.InstanceSubscriptionTimeout, TimeSpan.FromSeconds(1), nameof(options));

        Sqs = sqs;
        Sns = sns;
        _ownsClients = ownsClients;
        _names = new SqsMessageTransportNames(Options.NamePrefix, Options.Fifo);
        _queueAttributes = new Dictionary<string, string>
        {
            [QueueAttributeName.MessageRetentionPeriod] = ((int)MessageRetention.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        };
        if (Options.Fifo)
            _queueAttributes[QueueAttributeName.FifoQueue] = "true";

        TimeProvider = Options.TimeProvider ?? TimeProvider.System;
        Logger = (Options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SqsMessageTransport>();
        Capabilities = CreateCapabilities(Options.Fifo);
    }

    /// <summary>
    /// Standard queues delay sends natively for up to 15 minutes. FIFO queues, and every publish, are delayed by hiding
    /// the message once received, for up to 13 days; on FIFO queues that holds back the later messages of its group.
    /// Retries are delayed with a visibility timeout of up to 12 hours from the receive. Messages may be 252 KiB as
    /// <see cref="MessageSize"/> measures them: SQS and SNS accept 256 KiB including the transport's envelope, which
    /// keeps 4 KiB for framing and the dead-letter reason. FIFO mode orders groups (dead letters excepted) and
    /// deduplicates within 5 minutes.
    /// </summary>
    public TransportCapabilities Capabilities { get; }

    internal SqsMessageTransportOptions Options { get; }
    internal IAmazonSQS Sqs { get; }
    internal IAmazonSimpleNotificationService Sns { get; }
    internal TimeProvider TimeProvider { get; }
    internal ILogger Logger { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public async Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return;

        var now = TimeProvider.GetUtcNow();
        var encoded = messages.Select(m => Encode(destination, m, now)).ToList();
        try
        {
            switch (destination.Kind)
            {
                case MessageDestinationKind.Queue:
                    await SendToQueueAsync(_names.Queue(destination.Name), encoded, cancellationToken).AnyContext();
                    break;
                case MessageDestinationKind.Subscription:
                    string queueName = _names.Subscription(destination.Name, destination.SubscriptionName!);
                    await SubscribeAsync(destination.Name, queueName, cancellationToken).AnyContext();
                    await SendToQueueAsync(queueName, encoded, cancellationToken).AnyContext();
                    break;
                default:
                    await PublishAsync(_names.Topic(destination.Name), encoded, cancellationToken).AnyContext();
                    break;
            }
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Could not send to {destination}: {ex.Message}", ex);
        }
    }

    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseDuration, TimeSpan.Zero, nameof(options));
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        SqsMessageReceiver receiver;
        try
        {
            receiver = source.Kind switch
            {
                MessageSourceKind.InstanceSubscription => await CreateInstanceReceiverAsync(source, options, cancellationToken).AnyContext(),
                MessageSourceKind.Subscription => await CreateSubscriptionReceiverAsync(source, options, cancellationToken).AnyContext(),
                MessageSourceKind.DeadLetter => await CreateQueueReceiverAsync(source, options, _names.DeadLetter(source), cancellationToken).AnyContext(),
                _ => await CreateQueueReceiverAsync(source, options, _names.Queue(source.Name), cancellationToken).AnyContext()
            };
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Could not open a receiver for {source}: {ex.Message}", ex);
        }

        _receivers.TryAdd(receiver, 0);
        receiver.Start();
        return receiver;
    }

    /// <summary>
    /// Removes the source's messages and dead letters, and unsubscribes a durable subscription from its topic, but keeps
    /// the queues, so the source can be used again at once. Messages that were in flight or delayed during the deletion
    /// are discarded by receivers opened afterwards, using the deletion time tagged on the queue. On FIFO queues, SQS
    /// keeps dropping deduplication ids seen in the 5 minutes before the deletion.
    /// </summary>
    public async Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source));

        try
        {
            switch (source.Kind)
            {
                case MessageSourceKind.Queue:
                    await PurgeAsync(_names.Queue(source.Name), cancellationToken).AnyContext();
                    break;
                case MessageSourceKind.Subscription:
                    string queueName = _names.Subscription(source.Topic!, source.Name);
                    await UnsubscribeAsync(source.Topic!, queueName, cancellationToken).AnyContext();
                    await PurgeAsync(queueName, cancellationToken).AnyContext();
                    break;
            }

            await PurgeAsync(_names.DeadLetter(source), cancellationToken).AnyContext();
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Could not delete {source}: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var receiver in _receivers.Keys)
            await receiver.DisposeAsync().AnyContext();

        if (_ownsClients)
        {
            Sqs.Dispose();
            Sns.Dispose();
        }
    }

    internal void Untrack(SqsMessageReceiver receiver) => _receivers.TryRemove(receiver, out _);

    internal static bool IsLeaseLost(AmazonServiceException ex) =>
        ex is ReceiptHandleIsInvalidException or MessageNotInflightException || IsLeaseLost(ex.ErrorCode);

    /// <summary>
    /// SQS reports a receipt handle that expired, was replaced by a newer delivery or belongs to a deleted message
    /// with one of these codes. <c>InvalidParameterValue</c> is what real SQS returns for an expired receipt handle.
    /// </summary>
    internal static bool IsLeaseLost(string? errorCode) => errorCode is "ReceiptHandleIsInvalid" or "MessageNotInflight"
        or "AWS.SimpleQueueService.MessageNotInflight" or "InvalidParameterValue" or "InvalidParameterValueException";

    internal static long? GetNumber(Dictionary<string, string>? attributes, string name) =>
        attributes?.GetValueOrDefault(name) is { } value && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : null;

    /// <summary>
    /// Copies a delivery to a dead-letter queue with the reason. Dead letters are not ordered, so on FIFO queues each
    /// is its own message group and a group's dead letters can be inspected together. The copy is deduplicated by the
    /// delivery, so a retried dead-lettering is not copied twice but a message dead-lettered again after a replay is.
    /// </summary>
    internal async Task SendDeadLetterAsync(string deadLetterQueueName, IncomingMessage message, string deliveryId, string reason, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(message.Headers, StringComparer.Ordinal)
        {
            [KnownHeaders.DeadLetterReason] = SqsMessageTransportCodec.TruncateUtf8(reason ?? String.Empty, SqsMessageTransportCodec.MaxDeadLetterReasonBytes)
        };

        var deadLetter = new OutgoingMessage { MessageId = message.MessageId, Body = message.Body, Headers = headers, GroupKey = message.GroupKey };
        var encoded = SqsMessageTransportCodec.Encode(deadLetter, null);
        if (Options.Fifo)
        {
            string id = SqsMessageTransportNames.FifoId(deliveryId);
            encoded = encoded with { GroupId = id, DeduplicationId = id };
        }

        await SendToQueueAsync(deadLetterQueueName, [encoded], cancellationToken).AnyContext();
    }

    internal Task HeartbeatAsync(string queueUrl, CancellationToken cancellationToken) =>
        Sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = queueUrl, Tags = new Dictionary<string, string> { [HeartbeatTag] = GetHeartbeat() } }, cancellationToken);

    internal async Task RemoveInstanceAsync(SqsInstanceSubscription instance)
    {
        await UnsubscribeAsync(instance.SubscriptionArn).AnyContext();
        await DeleteQueueAsync(instance.QueueUrl).AnyContext();
    }

    private static TransportCapabilities CreateCapabilities(bool fifo) => new()
    {
        MaxDelay = fifo ? MaxEmulatedDelay : MaxNativeDelay,
        MaxPublishDelay = MaxEmulatedDelay,
        MaxRetryDelay = TimeSpan.FromSeconds(MaxVisibilityTimeoutSeconds),
        MinRetryDelay = TimeSpan.FromSeconds(1), // visibility timeouts are whole seconds
        MaxSendBatchSize = MaxBatchSize,
        MaxReceiveBatchSize = MaxBatchSize,
        MaxMessageSize = SqsMessageTransportCodec.MaxMessageSize,
        MaxLeaseDuration = TimeSpan.FromSeconds(MaxVisibilityTimeoutSeconds),
        LeaseExpiration = true,
        OrderedGroups = fifo,
        Deduplication = fifo
    };

    private SqsEncodedMessage Encode(MessageDestination destination, OutgoingMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message, nameof(message));
        long size = MessageSize.Of(message);
        if (size > SqsMessageTransportCodec.MaxMessageSize)
        {
            throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes with its headers and ids, which exceeds the SQS transport limit of {SqsMessageTransportCodec.MaxMessageSize} bytes.")
            {
                Size = size,
                Limit = SqsMessageTransportCodec.MaxMessageSize
            };
        }

        // Standard queues delay natively; a delay SQS cannot apply travels in the envelope and the receiver hides the
        // message until it is due.
        bool nativeDelay = destination.Kind != MessageDestinationKind.Topic && !Options.Fifo;
        var deliverAt = message.DeliverAt > now ? message.DeliverAt : null;
        var hideUntil = deliverAt is { } at && (!nativeDelay || at - now > MaxNativeDelay) ? deliverAt : null;
        var encoded = SqsMessageTransportCodec.Encode(message, hideUntil) with { DelaySeconds = nativeDelay ? GetDelaySeconds(deliverAt, now) : 0 };
        if (encoded.Size > SqsMessageTransportCodec.MaxBrokerMessageSize)
        {
            throw new MessageTooLargeException($"Message {message.MessageId} is {encoded.Size} bytes once encoded, over the {SqsMessageTransportCodec.MaxBrokerMessageSize} bytes SQS and SNS accept: its {message.Headers.Count} headers need more framing than the transport reserves.")
            {
                Size = encoded.Size,
                Limit = SqsMessageTransportCodec.MaxBrokerMessageSize
            };
        }

        if (!Options.Fifo)
            return encoded;

        // An ungrouped message is its own group, so it is not held behind others. Without a deduplication id, a random
        // one makes SQS drop only the retries of this very send.
        return encoded with
        {
            GroupId = SqsMessageTransportNames.FifoId(message.GroupKey ?? message.MessageId),
            DeduplicationId = message.DeduplicationId is { } deduplicationId ? SqsMessageTransportNames.FifoId(deduplicationId) : Guid.NewGuid().ToString("N")
        };
    }

    private async Task<SqsMessageReceiver> CreateQueueReceiverAsync(MessageSource source, ReceiverOptions options, string queueName, CancellationToken cancellationToken)
    {
        string queueUrl = await GetQueueUrlAsync(queueName, cancellationToken).AnyContext();
        long? purgedBefore = await GetPurgedBeforeAsync(queueUrl, cancellationToken).AnyContext();
        return new SqsMessageReceiver(this, source, options, queueUrl, _names.DeadLetter(source), null, purgedBefore);
    }

    private async Task<SqsMessageReceiver> CreateSubscriptionReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken)
    {
        string queueName = _names.Subscription(source.Topic!, source.Name);
        await SubscribeAsync(source.Topic!, queueName, cancellationToken).AnyContext();
        return await CreateQueueReceiverAsync(source, options, queueName, cancellationToken).AnyContext();
    }

    private async Task<SqsMessageReceiver> CreateInstanceReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken)
    {
        string topicName = _names.Topic(source.Topic!);
        string topicArn = await GetTopicArnAsync(topicName, cancellationToken).AnyContext();
        string instancePrefix = _names.InstancePrefix(source.Topic!);
        string queueName = _names.NewInstance(instancePrefix, source.Name);

        var created = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = Options.Fifo ? new Dictionary<string, string> { [QueueAttributeName.FifoQueue] = "true" } : null,
            Tags = new Dictionary<string, string> { [TopicTag] = topicName, [HeartbeatTag] = GetHeartbeat() }
        }, cancellationToken).AnyContext();

        string subscriptionArn;
        try
        {
            subscriptionArn = await SubscribeQueueAsync(created.QueueUrl, topicArn, cancellationToken).AnyContext();
        }
        catch
        {
            await DeleteQueueAsync(created.QueueUrl).AnyContext();
            throw;
        }

        Logger.LogDebug("Created instance subscription queue {QueueName} for topic {Topic}", queueName, topicName);
        var instance = new SqsInstanceSubscription(topicArn, topicName, instancePrefix, queueName, created.QueueUrl, subscriptionArn);
        await SweepInstancesAsync(instance).AnyContext();
        return new SqsMessageReceiver(this, source, options, created.QueueUrl, null, instance, null);
    }

    private async Task SendToQueueAsync(string queueName, IReadOnlyList<SqsEncodedMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var batch in Batch(messages))
        {
            var pending = batch;
            for (int attempt = 1; ; attempt++)
            {
                string queueUrl = await GetQueueUrlAsync(queueName, cancellationToken).AnyContext();
                SendMessageBatchResponse response;
                try
                {
                    response = await Sqs.SendMessageBatchAsync(new SendMessageBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = pending.Select((m, i) => new SendMessageBatchRequestEntry
                        {
                            Id = i.ToString(CultureInfo.InvariantCulture),
                            MessageBody = m.Body,
                            DelaySeconds = m.DelaySeconds > 0 ? m.DelaySeconds : null,
                            MessageGroupId = m.GroupId,
                            MessageDeduplicationId = m.DeduplicationId,
                            MessageAttributes = SqsMessageTransportCodec.ToSqsAttributes(m)
                        }).ToList()
                    }, cancellationToken).AnyContext();
                }
                catch (QueueDoesNotExistException) when (attempt < MaxSendAttempts)
                {
                    Logger.LogDebug("Queue {QueueName} no longer exists; creating it again", queueName);
                    _queueUrls.TryRemove(queueName, out _);
                    continue;
                }

                pending = await GetRetryableFailuresAsync(pending, response.Failed?.Select(f => (f.Id, f.Code, f.Message, f.SenderFault)), attempt, $"queue {queueName}", cancellationToken).AnyContext();
                if (pending.Count == 0)
                    break;
            }
        }
    }

    private async Task PublishAsync(string topicName, IReadOnlyList<SqsEncodedMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var batch in Batch(messages))
        {
            var pending = batch;
            for (int attempt = 1; ; attempt++)
            {
                string topicArn = await GetTopicArnAsync(topicName, cancellationToken).AnyContext();
                PublishBatchResponse response;
                try
                {
                    response = await Sns.PublishBatchAsync(new PublishBatchRequest
                    {
                        TopicArn = topicArn,
                        PublishBatchRequestEntries = pending.Select((m, i) => new PublishBatchRequestEntry
                        {
                            Id = i.ToString(CultureInfo.InvariantCulture),
                            Message = m.Body,
                            MessageGroupId = m.GroupId,
                            MessageDeduplicationId = m.DeduplicationId,
                            MessageAttributes = SqsMessageTransportCodec.ToSnsAttributes(m)
                        }).ToList()
                    }, cancellationToken).AnyContext();
                }
                catch (SnsNotFoundException) when (attempt < MaxSendAttempts)
                {
                    Logger.LogDebug("Topic {Topic} no longer exists; creating it again", topicName);
                    _topicArns.TryRemove(topicName, out _);
                    continue;
                }

                pending = await GetRetryableFailuresAsync(pending, response.Failed?.Select(f => (f.Id, f.Code, f.Message, f.SenderFault)), attempt, $"topic {topicName}", cancellationToken).AnyContext();
                if (pending.Count == 0)
                    break;
            }
        }
    }

    private static async Task<IReadOnlyList<SqsEncodedMessage>> GetRetryableFailuresAsync(IReadOnlyList<SqsEncodedMessage> sent,
        IEnumerable<(string Id, string Code, string Message, bool? SenderFault)>? failures, int attempt, string destination, CancellationToken cancellationToken)
    {
        var failed = failures?.ToList() ?? [];
        if (failed.Count == 0)
            return [];

        if (attempt >= MaxSendAttempts || failed.Any(f => f.SenderFault is true))
        {
            var first = failed[0];
            throw new MessageBusException($"Could not send {failed.Count} of {sent.Count} messages to {destination}: {first.Code} {first.Message}");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).AnyContext();
        return failed.Select(f => sent[Int32.Parse(f.Id, CultureInfo.InvariantCulture)]).ToList();
    }

    /// <summary>Splits messages into batches SQS and SNS accept: at most 10 entries and 256 KiB in total.</summary>
    private static IEnumerable<IReadOnlyList<SqsEncodedMessage>> Batch(IReadOnlyList<SqsEncodedMessage> messages)
    {
        var batch = new List<SqsEncodedMessage>(Math.Min(messages.Count, MaxBatchSize));
        int size = 0;
        foreach (var message in messages)
        {
            if (batch.Count > 0 && (batch.Count == MaxBatchSize || size + message.Size > MaxBatchPayloadSize))
            {
                yield return batch;
                batch = new List<SqsEncodedMessage>(MaxBatchSize);
                size = 0;
            }

            batch.Add(message);
            size += message.Size;
        }

        if (batch.Count > 0)
            yield return batch;
    }

    private Task<string> GetQueueUrlAsync(string queueName, CancellationToken cancellationToken) =>
        GetOrCreateAsync(_queueUrls, queueName, () => ResolveQueueUrlAsync(queueName), cancellationToken);

    /// <summary>Returns the queue's URL without creating it, or <c>null</c> when it does not exist.</summary>
    private async Task<string?> FindQueueUrlAsync(string queueName, CancellationToken cancellationToken)
    {
        if (await GetCachedAsync(_queueUrls, queueName).AnyContext() is { } cached)
            return cached;

        try
        {
            return (await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }, cancellationToken).AnyContext()).QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            return null;
        }
    }

    private Task<string> GetTopicArnAsync(string topicName, CancellationToken cancellationToken) =>
        GetOrCreateAsync(_topicArns, topicName, async () =>
        {
            var response = await Sns.CreateTopicAsync(new CreateTopicRequest
            {
                Name = topicName,
                Attributes = Options.Fifo ? new Dictionary<string, string> { ["FifoTopic"] = "true" } : null
            }).AnyContext();

            Logger.LogDebug("Using topic {TopicArn}", response.TopicArn);
            return response.TopicArn;
        }, cancellationToken);

    private async Task<string> ResolveQueueUrlAsync(string queueName)
    {
        try
        {
            return (await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }).AnyContext()).QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
        }

        try
        {
            var response = await Sqs.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>(_queueAttributes)
            }).AnyContext();

            Logger.LogDebug("Created queue {QueueName}", queueName);
            return response.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            return (await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }).AnyContext()).QueueUrl;
        }
    }

    /// <summary>
    /// The time of the queue's last purge, on the queue's own clock: receivers discard messages sent before it. Read
    /// when a receiver opens; a receiver left open across a deletion keeps delivering its source's older messages.
    /// </summary>
    private async Task<long?> GetPurgedBeforeAsync(string queueUrl, CancellationToken cancellationToken)
    {
        var tags = (await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = queueUrl }, cancellationToken).AnyContext()).Tags;
        return GetNumber(tags, PurgedBeforeTag);
    }

    /// <summary>
    /// Subscribes the queue to the topic, creating both if needed. Done on every receiver and every send to the
    /// subscription, not cached, so a subscription deleted by another process is restored; SNS returns the existing
    /// subscription when there is one.
    /// </summary>
    private async Task SubscribeAsync(string topic, string queueName, CancellationToken cancellationToken)
    {
        string topicArn = await GetTopicArnAsync(_names.Topic(topic), cancellationToken).AnyContext();
        string queueUrl = await GetQueueUrlAsync(queueName, cancellationToken).AnyContext();
        await SubscribeQueueAsync(queueUrl, topicArn, cancellationToken).AnyContext();
    }

    /// <summary>Allows the topic to send to the queue, keeping the queue's other policy statements, then subscribes it with raw delivery.</summary>
    private async Task<string> SubscribeQueueAsync(string queueUrl, string topicArn, CancellationToken cancellationToken)
    {
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = [QueueAttributeName.QueueArn, QueueAttributeName.Policy]
        }, cancellationToken).AnyContext();

        string queueArn = attributes.Attributes?.GetValueOrDefault(QueueAttributeName.QueueArn)
            ?? throw new MessageBusException($"Queue {queueUrl} did not report its ARN.");

        string? policy = SqsMessageTransportPolicy.Merge(attributes.Attributes.GetValueOrDefault(QueueAttributeName.Policy), queueArn, topicArn);
        if (policy is not null)
        {
            await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string> { [QueueAttributeName.Policy] = policy }
            }, cancellationToken).AnyContext();
        }

        try
        {
            var response = await Sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn,
                ReturnSubscriptionArn = true,
                Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" }
            }, cancellationToken).AnyContext();

            return response.SubscriptionArn;
        }
        catch (InvalidParameterException)
        {
            // The queue may already be subscribed with other attributes, for example without raw delivery.
            string? existing = (await ListSubscriptionsAsync(topicArn, MaxSubscriptionPagesPerDelete, cancellationToken).AnyContext())
                .FirstOrDefault(s => s.Protocol == "sqs" && s.Endpoint == queueArn)?.SubscriptionArn;
            if (existing is null)
                throw;

            await Sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = existing,
                AttributeName = "RawMessageDelivery",
                AttributeValue = "true"
            }, cancellationToken).AnyContext();

            return existing;
        }
    }

    /// <summary>
    /// Removes every subscription of the topic that delivers to the queue, then the topic's permission to send to it,
    /// so a subscription SNS has not finished removing cannot deliver either. Creates nothing.
    /// </summary>
    private async Task UnsubscribeAsync(string topic, string queueName, CancellationToken cancellationToken)
    {
        string? queueUrl = await FindQueueUrlAsync(queueName, cancellationToken).AnyContext();
        if (queueUrl is null)
            return;

        GetQueueAttributesResponse attributes;
        try
        {
            attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = [QueueAttributeName.QueueArn, QueueAttributeName.Policy]
            }, cancellationToken).AnyContext();
        }
        catch (QueueDoesNotExistException)
        {
            return;
        }

        string? queueArn = attributes.Attributes?.GetValueOrDefault(QueueAttributeName.QueueArn);
        if (queueArn is null)
            return;

        string topicName = _names.Topic(topic);
        string? topicArn = await GetCachedAsync(_topicArns, topicName).AnyContext() ?? GetTopicArn(queueArn, topicName);
        if (topicArn is null)
            return;

        foreach (var subscription in await ListSubscriptionsAsync(topicArn, MaxSubscriptionPagesPerDelete, cancellationToken).AnyContext())
        {
            if (subscription.Protocol == "sqs" && subscription.Endpoint == queueArn)
                await UnsubscribeAsync(subscription.SubscriptionArn).AnyContext();
        }

        string? policy = SqsMessageTransportPolicy.Remove(attributes.Attributes!.GetValueOrDefault(QueueAttributeName.Policy), queueArn, topicArn);
        if (policy is not null)
        {
            await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string> { [QueueAttributeName.Policy] = policy }
            }, cancellationToken).AnyContext();
        }

        Logger.LogDebug("Unsubscribed queue {QueueName} from topic {Topic}", queueName, topicName);
    }

    /// <summary>
    /// Deletes the messages the queue held when the purge started, without deleting the queue, and tags the queue with
    /// the purge's time on the queue's own clock so receivers discard older messages the purge did not see (delayed,
    /// hidden until due, or held by a receiver). Messages sent during the purge are kept. SQS's PurgeQueue is not
    /// used: it runs once a minute at most and may also delete messages sent during the following minute.
    /// </summary>
    private async Task PurgeAsync(string queueName, CancellationToken cancellationToken)
    {
        string? queueUrl = await FindQueueUrlAsync(queueName, cancellationToken).AnyContext();
        if (queueUrl is null)
            return;

        try
        {
            var purge = await SqsMessageTransportPurge.RunAsync(Sqs, queueUrl, Options.Fifo, Logger, cancellationToken).AnyContext();
            if (purge.PurgedBefore is { } before)
            {
                await Sqs.TagQueueAsync(new TagQueueRequest
                {
                    QueueUrl = queueUrl,
                    Tags = new Dictionary<string, string> { [PurgedBeforeTag] = before.ToString(CultureInfo.InvariantCulture) }
                }, cancellationToken).AnyContext();
            }
            else
            {
                Logger.LogWarning("Purged queue {QueueName} without receiving its marker; messages that were in flight or delayed may still be delivered", queueName);
            }

            Logger.LogDebug("Purged {Count} messages from queue {QueueName}", purge.Deleted, queueName);
        }
        catch (QueueDoesNotExistException)
        {
            _queueUrls.TryRemove(queueName, out _);
        }
    }

    /// <summary>The ARN of a topic in the queue's account and region, built without creating the topic.</summary>
    private static string? GetTopicArn(string queueArn, string topicName)
    {
        string[] parts = queueArn.Split(':');
        return parts.Length == 6 ? $"{parts[0]}:{parts[1]}:sns:{parts[3]}:{parts[4]}:{topicName}" : null;
    }

    private async Task<IReadOnlyList<Subscription>> ListSubscriptionsAsync(string topicArn, int maxPages, CancellationToken cancellationToken = default)
    {
        var subscriptions = new List<Subscription>();
        string? nextToken = null;
        for (int page = 0; page < maxPages; page++)
        {
            ListSubscriptionsByTopicResponse response;
            try
            {
                response = await Sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = nextToken }, cancellationToken).AnyContext();
            }
            catch (SnsNotFoundException)
            {
                break;
            }

            if (response.Subscriptions is not null)
                subscriptions.AddRange(response.Subscriptions);

            nextToken = response.NextToken;
            if (String.IsNullOrEmpty(nextToken))
                break;
        }

        return subscriptions;
    }

    /// <summary>
    /// Removes instance queues of the topic whose heartbeat lapsed. Called when an instance receiver opens and on its
    /// heartbeats, but runs at most once per <see cref="SweepIntervalInTimeouts"/> instance timeouts per topic and
    /// transport, and checks at most <see cref="MaxInstanceChecksPerSweep"/> queues, so the cost grows linearly with
    /// the number of instances rather than quadratically.
    /// </summary>
    internal async Task SweepInstancesAsync(SqsInstanceSubscription own)
    {
        long now = Stopwatch.GetTimestamp();
        long last = _lastSweeps.GetOrAdd(own.TopicName, 0);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < Options.InstanceSubscriptionTimeout * SweepIntervalInTimeouts)
            return;
        if (!_lastSweeps.TryUpdate(own.TopicName, now, last))
            return;

        try
        {
            var listed = await Sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = own.InstancePrefix, MaxResults = 1000 }).AnyContext();
            var candidates = (listed.QueueUrls ?? []).Where(url => GetQueueName(url) != own.QueueName).ToArray();
            Random.Shared.Shuffle(candidates);

            long staleBefore = TimeProvider.GetUtcNow().Subtract(Options.InstanceSubscriptionTimeout).ToUnixTimeSeconds();
            var checks = await Task.WhenAll(candidates.Take(MaxInstanceChecksPerSweep).Select(async queueUrl =>
            {
                Dictionary<string, string>? tags;
                try
                {
                    tags = (await Sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = queueUrl }).AnyContext()).Tags;
                }
                catch (QueueDoesNotExistException)
                {
                    return null;
                }

                if (tags?.GetValueOrDefault(TopicTag) != own.TopicName)
                    return null;

                bool isStale = !Int64.TryParse(tags.GetValueOrDefault(HeartbeatTag), NumberStyles.Integer, CultureInfo.InvariantCulture, out long heartbeat) || heartbeat < staleBefore;
                return isStale ? queueUrl : null;
            })).AnyContext();

            var stale = checks.OfType<string>().ToList();
            if (stale.Count == 0)
                return;

            var subscriptions = await ListSubscriptionsAsync(own.TopicArn, MaxSubscriptionPagesPerSweep).AnyContext();
            foreach (string queueUrl in stale)
            {
                string queueName = GetQueueName(queueUrl);
                foreach (var subscription in subscriptions.Where(s => s.Protocol == "sqs" && s.Endpoint?.EndsWith(":" + queueName, StringComparison.Ordinal) == true))
                    await UnsubscribeAsync(subscription.SubscriptionArn).AnyContext();

                await DeleteQueueAsync(queueUrl).AnyContext();
            }

            Logger.LogInformation("Removed {Count} stale instance subscriptions of topic {Topic}", stale.Count, own.TopicName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove stale instance subscriptions of topic {Topic}", own.TopicName);
        }
    }

    private async Task UnsubscribeAsync(string subscriptionArn)
    {
        try
        {
            await Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subscriptionArn }).AnyContext();
        }
        catch (SnsNotFoundException)
        {
        }
    }

    private async Task DeleteQueueAsync(string queueUrl)
    {
        try
        {
            await Sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }).AnyContext();
        }
        catch (QueueDoesNotExistException)
        {
        }
    }

    private string GetHeartbeat() => TimeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string GetQueueName(string queueUrl) => queueUrl[(queueUrl.LastIndexOf('/') + 1)..];

    private static int GetDelaySeconds(DateTimeOffset? deliverAt, DateTimeOffset now)
    {
        if (deliverAt is not { } at || at <= now)
            return 0;

        return (int)Math.Min(Math.Ceiling((at - now).TotalSeconds), MaxNativeDelay.TotalSeconds);
    }

    /// <summary>A value already resolved by <see cref="GetOrCreateAsync"/>, without resolving it.</summary>
    private static async Task<string?> GetCachedAsync(ConcurrentDictionary<string, Lazy<Task<string>>> cache, string key)
    {
        if (!cache.TryGetValue(key, out var entry) || !entry.IsValueCreated)
            return null;

        var task = entry.Value;
        return task.IsCompletedSuccessfully ? await task.AnyContext() : null;
    }

    private static async Task<string> GetOrCreateAsync(ConcurrentDictionary<string, Lazy<Task<string>>> cache, string key, Func<Task<string>> factory, CancellationToken cancellationToken)
    {
        var entry = cache.GetOrAdd(key, static (_, create) => new Lazy<Task<string>>(create), factory);
        try
        {
            return await entry.Value.WaitAsync(cancellationToken).AnyContext();
        }
        catch when (entry.Value.IsFaulted || entry.Value.IsCanceled)
        {
            cache.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(key, entry));
            throw;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private static IAmazonSQS CreateSqsClient(SqsMessageTransportOptions options)
    {
        var (credentials, region, serviceUrl) = GetConnection(options);
        return serviceUrl is null
            ? new AmazonSQSClient(credentials, region)
            : new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = serviceUrl, AuthenticationRegion = region?.SystemName ?? RegionEndpoint.USEast1.SystemName });
    }

    private static IAmazonSimpleNotificationService CreateSnsClient(SqsMessageTransportOptions options)
    {
        var (credentials, region, serviceUrl) = GetConnection(options);
        return serviceUrl is null
            ? new AmazonSimpleNotificationServiceClient(credentials, region)
            : new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl, AuthenticationRegion = region?.SystemName ?? RegionEndpoint.USEast1.SystemName });
    }

    private static (AWSCredentials Credentials, RegionEndpoint? Region, string? ServiceUrl) GetConnection(SqsMessageTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = String.IsNullOrEmpty(options.ConnectionString)
            ? new SqsMessageTransportConnectionStringBuilder()
            : new SqsMessageTransportConnectionStringBuilder(options.ConnectionString);

        var credentials = builder.GetCredentials() ?? DefaultAWSCredentialsIdentityResolver.GetCredentials();
        var region = builder.GetRegion() ?? (String.IsNullOrEmpty(builder.ServiceUrl) ? FallbackRegionFactory.GetRegionEndpoint() : null);
        return (credentials, region, String.IsNullOrEmpty(builder.ServiceUrl) ? null : builder.ServiceUrl);
    }
}

internal sealed record SqsInstanceSubscription(string TopicArn, string TopicName, string InstancePrefix, string QueueName, string QueueUrl, string SubscriptionArn);
