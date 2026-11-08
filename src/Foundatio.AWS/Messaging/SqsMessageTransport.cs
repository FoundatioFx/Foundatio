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
/// tag goes stale. Every source has its own dead-letter queue with 14-day retention. Leases are visibility timeouts.
/// Queues, topics, subscriptions and queue policies are created on first use.
/// </summary>
/// <remarks>
/// SNS cannot delay a publish, so a topic message with <see cref="OutgoingMessage.DeliverAt"/> is published at once and
/// each subscription's receiver hides it until that time.
/// </remarks>
public sealed class SqsMessageTransport : IMessageTransport
{
    internal const int MaxBatchSize = 10;
    internal const string HeartbeatTag = "foundatio-heartbeat";
    internal const string TopicTag = "foundatio-topic";
    private const int MaxBatchPayloadSize = 256 * 1024;
    private const int MaxSendAttempts = 3;
    private const int MaxDeadLetterReasonLength = 1024;
    private const int MaxInstanceChecksPerSweep = 20;
    private const int MaxSubscriptionPagesPerSweep = 10;
    private const int SweepIntervalInTimeouts = 6;

    private static readonly Dictionary<string, string> DeadLetterQueueAttributes = new()
    {
        [QueueAttributeName.MessageRetentionPeriod] = ((int)TimeSpan.FromDays(14).TotalSeconds).ToString(CultureInfo.InvariantCulture)
    };

    private readonly bool _ownsClients;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _queueUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _topicArns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _subscriptions = new(StringComparer.Ordinal);
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
        TimeProvider = Options.TimeProvider ?? TimeProvider.System;
        Logger = (Options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SqsMessageTransport>();
    }

    public TransportCapabilities Capabilities { get; } = new()
    {
        MaxDelay = TimeSpan.FromMinutes(15),
        MaxSendBatchSize = MaxBatchSize,
        MaxReceiveBatchSize = MaxBatchSize,
        // SQS and SNS accept 256 KiB including attributes. A binary body grows by a third as base64, and the header
        // envelope needs room, so this is the largest message that always fits.
        MaxMessageSize = 190 * 1024,
        MaxLeaseDuration = TimeSpan.FromHours(12),
        LeaseExpiration = true,
        OrderedGroups = false,
        Deduplication = false
    };

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
        try
        {
            if (destination.Kind == MessageDestinationKind.Queue)
            {
                var encoded = messages.Select(m => SqsMessageTransportCodec.Encode(m, null) with { DelaySeconds = GetDelaySeconds(m.DeliverAt, now) }).ToList();
                await SendToQueueAsync(SqsMessageTransportNames.Queue(Options.NamePrefix, destination.Name), null, encoded, cancellationToken).AnyContext();
            }
            else
            {
                var encoded = messages.Select(m => SqsMessageTransportCodec.Encode(m, m.DeliverAt > now ? m.DeliverAt : null)).ToList();
                await PublishAsync(SqsMessageTransportNames.Topic(Options.NamePrefix, destination.Name), encoded, cancellationToken).AnyContext();
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
                MessageSourceKind.DeadLetter => new SqsMessageReceiver(this, source, options,
                    await GetQueueUrlAsync(SqsMessageTransportNames.DeadLetter(Options.NamePrefix, source), DeadLetterQueueAttributes, cancellationToken).AnyContext(),
                    SqsMessageTransportNames.DeadLetter(Options.NamePrefix, source), null),
                _ => new SqsMessageReceiver(this, source, options,
                    await GetQueueUrlAsync(SqsMessageTransportNames.Queue(Options.NamePrefix, source.Name), null, cancellationToken).AnyContext(),
                    SqsMessageTransportNames.DeadLetter(Options.NamePrefix, source), null)
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

    internal async Task SendDeadLetterAsync(string deadLetterQueueName, IncomingMessage message, string reason, CancellationToken cancellationToken)
    {
        reason ??= String.Empty;
        var headers = new Dictionary<string, string>(message.Headers, StringComparer.Ordinal)
        {
            [KnownHeaders.DeadLetterReason] = reason.Length > MaxDeadLetterReasonLength ? reason[..MaxDeadLetterReasonLength] : reason
        };

        var deadLetter = new OutgoingMessage { MessageId = message.MessageId, Body = message.Body, Headers = headers, GroupKey = message.GroupKey };
        await SendToQueueAsync(deadLetterQueueName, DeadLetterQueueAttributes, [SqsMessageTransportCodec.Encode(deadLetter, null)], cancellationToken).AnyContext();
    }

    internal Task HeartbeatAsync(string queueUrl, CancellationToken cancellationToken) =>
        Sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = queueUrl, Tags = new Dictionary<string, string> { [HeartbeatTag] = GetHeartbeat() } }, cancellationToken);

    internal async Task RemoveInstanceAsync(SqsInstanceSubscription instance)
    {
        await UnsubscribeAsync(instance.SubscriptionArn).AnyContext();
        await DeleteQueueAsync(instance.QueueUrl).AnyContext();
    }

    private async Task<SqsMessageReceiver> CreateSubscriptionReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken)
    {
        string topicArn = await GetTopicArnAsync(SqsMessageTransportNames.Topic(Options.NamePrefix, source.Topic!), cancellationToken).AnyContext();
        string queueName = SqsMessageTransportNames.Subscription(Options.NamePrefix, source.Topic!, source.Name);
        string queueUrl = await GetQueueUrlAsync(queueName, null, cancellationToken).AnyContext();
        await GetOrCreateAsync(_subscriptions, $"{queueName}|{topicArn}", () => SubscribeAsync(queueUrl, topicArn), cancellationToken).AnyContext();

        return new SqsMessageReceiver(this, source, options, queueUrl, SqsMessageTransportNames.DeadLetter(Options.NamePrefix, source), null);
    }

    private async Task<SqsMessageReceiver> CreateInstanceReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken)
    {
        string topicName = SqsMessageTransportNames.Topic(Options.NamePrefix, source.Topic!);
        string topicArn = await GetTopicArnAsync(topicName, cancellationToken).AnyContext();
        string instancePrefix = SqsMessageTransportNames.InstancePrefix(Options.NamePrefix, source.Topic!);
        string queueName = SqsMessageTransportNames.NewInstance(instancePrefix, source.Name);

        var created = await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Tags = new Dictionary<string, string> { [TopicTag] = topicName, [HeartbeatTag] = GetHeartbeat() }
        }, cancellationToken).AnyContext();

        string subscriptionArn;
        try
        {
            subscriptionArn = await SubscribeAsync(created.QueueUrl, topicArn).AnyContext();
        }
        catch
        {
            await DeleteQueueAsync(created.QueueUrl).AnyContext();
            throw;
        }

        Logger.LogDebug("Created instance subscription queue {QueueName} for topic {Topic}", queueName, topicName);
        var instance = new SqsInstanceSubscription(topicArn, topicName, instancePrefix, queueName, created.QueueUrl, subscriptionArn);
        await SweepInstancesAsync(instance).AnyContext();
        return new SqsMessageReceiver(this, source, options, created.QueueUrl, null, instance);
    }

    private async Task SendToQueueAsync(string queueName, IReadOnlyDictionary<string, string>? queueAttributes, IReadOnlyList<SqsEncodedMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var batch in Batch(messages))
        {
            var pending = batch;
            for (int attempt = 1; ; attempt++)
            {
                string queueUrl = await GetQueueUrlAsync(queueName, queueAttributes, cancellationToken).AnyContext();
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
                            DelaySeconds = m.DelaySeconds,
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

    private Task<string> GetQueueUrlAsync(string queueName, IReadOnlyDictionary<string, string>? attributes, CancellationToken cancellationToken) =>
        GetOrCreateAsync(_queueUrls, queueName, () => ResolveQueueUrlAsync(queueName, attributes), cancellationToken);

    private Task<string> GetTopicArnAsync(string topicName, CancellationToken cancellationToken) =>
        GetOrCreateAsync(_topicArns, topicName, async () =>
        {
            var response = await Sns.CreateTopicAsync(new CreateTopicRequest { Name = topicName }).AnyContext();
            Logger.LogDebug("Using topic {TopicArn}", response.TopicArn);
            return response.TopicArn;
        }, cancellationToken);

    private async Task<string> ResolveQueueUrlAsync(string queueName, IReadOnlyDictionary<string, string>? attributes)
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
                Attributes = attributes is null ? null : new Dictionary<string, string>(attributes)
            }).AnyContext();

            Logger.LogDebug("Created queue {QueueName}", queueName);
            return response.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            return (await Sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }).AnyContext()).QueueUrl;
        }
    }

    /// <summary>Allows the topic to send to the queue, keeping the queue's other policy statements, then subscribes it with raw delivery.</summary>
    private async Task<string> SubscribeAsync(string queueUrl, string topicArn)
    {
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = [QueueAttributeName.QueueArn, QueueAttributeName.Policy]
        }).AnyContext();

        string queueArn = attributes.Attributes?.GetValueOrDefault(QueueAttributeName.QueueArn)
            ?? throw new MessageBusException($"Queue {queueUrl} did not report its ARN.");

        string? policy = SqsMessageTransportPolicy.Merge(attributes.Attributes.GetValueOrDefault(QueueAttributeName.Policy), queueArn, topicArn);
        if (policy is not null)
        {
            await Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string> { [QueueAttributeName.Policy] = policy }
            }).AnyContext();
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
            }).AnyContext();

            return response.SubscriptionArn;
        }
        catch (InvalidParameterException)
        {
            // The queue may already be subscribed with other attributes, for example without raw delivery.
            string? existing = await FindSubscriptionAsync(topicArn, queueArn).AnyContext();
            if (existing is null)
                throw;

            await Sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = existing,
                AttributeName = "RawMessageDelivery",
                AttributeValue = "true"
            }).AnyContext();

            return existing;
        }
    }

    private async Task<string?> FindSubscriptionAsync(string topicArn, string queueArn)
    {
        var subscriptions = await ListSubscriptionsAsync(topicArn).AnyContext();
        return subscriptions.FirstOrDefault(s => s.Protocol == "sqs" && s.Endpoint == queueArn)?.SubscriptionArn;
    }

    private async Task<IReadOnlyList<Subscription>> ListSubscriptionsAsync(string topicArn)
    {
        var subscriptions = new List<Subscription>();
        string? nextToken = null;
        for (int page = 0; page < MaxSubscriptionPagesPerSweep; page++)
        {
            var response = await Sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = nextToken }).AnyContext();
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

            var subscriptions = await ListSubscriptionsAsync(own.TopicArn).AnyContext();
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

        return (int)Math.Min(Math.Ceiling((at - now).TotalSeconds), TimeSpan.FromMinutes(15).TotalSeconds);
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
