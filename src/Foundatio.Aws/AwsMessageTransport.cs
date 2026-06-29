using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;
using SqsMessage = Amazon.SQS.Model.Message;

namespace Foundatio.Messaging;

/// <summary>
/// An <see cref="IMessageTransport"/> over AWS SQS (queues + competing-consumer subscriptions) and SNS (topics). This
/// is a temporary in-repo provider used to validate the redesigned transport contract against a real broker. Queue and
/// subscription destinations are SQS queues; topic destinations are SNS topics fanned out to SQS subscription queues.
/// </summary>
/// <remarks>
/// Capability mapping: pull receive (SQS long poll), visibility timeout, redelivery delay (ChangeMessageVisibility,
/// 12h cap), delayed delivery (SQS DelaySeconds, 15-minute cap), provisioning, and stats. SQS has no per-message
/// priority, per-message TTL, or push delivery, and no transport-native dead-letter that the core controls the timing
/// of, so those capabilities are intentionally not implemented (the core owns retry/dead-lettering).
/// </remarks>
public sealed class AwsMessageTransport : IMessageTransport, ISupportsPull, ISupportsVisibilityTimeout,
    ISupportsLockRenewal, ISupportsRedeliveryDelay, ISupportsDelayedDelivery, ISupportsProvisioning, ISupportsStats, ITransportInfo
{
    private const string HeadersAttributeName = "fnd.headers";
    private const string EncodingAttributeName = "fnd.encoding";

    // Well-known headers surfaced as native message attributes (in addition to the authoritative JSON blob) so brokers
    // can filter/route on them — e.g. SNS subscription filter policies match on native attributes.
    private static readonly string[] WellKnownNativeHeaders = [KnownHeaders.MessageType, KnownHeaders.Priority, KnownHeaders.CorrelationId];

    private static readonly IReadOnlySet<DestinationRole> _supportedRoles =
        new HashSet<DestinationRole> { DestinationRole.Queue, DestinationRole.Topic, DestinationRole.Subscription, DestinationRole.Binding };

    private readonly AwsMessageTransportOptions _options;
    private readonly Lazy<IAmazonSQS> _sqs;
    private readonly Lazy<IAmazonSimpleNotificationService> _sns;
    private readonly ConcurrentDictionary<string, string> _queueUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _topicArns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DestinationRole> _roles = new(StringComparer.Ordinal);
    private int _isDisposed;

    public AwsMessageTransport(AwsMessageTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sqs = new Lazy<IAmazonSQS>(CreateSqsClient);
        _sns = new Lazy<IAmazonSimpleNotificationService>(CreateSnsClient);
    }

    public AwsMessageTransport(string connectionString) : this(AwsMessageTransportOptions.FromConnectionString(connectionString)) { }

    public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
    public OrderingGuarantee Ordering => OrderingGuarantee.None;
    public IReadOnlySet<DestinationRole> SupportedRoles => _supportedRoles;
    public int? MaxBatchSize => null; // sends are issued per message
    public long? MaxMessageBytes => 262144; // 256 KB SQS/SNS limit

    public TimeSpan? MaxDeliveryDelay => TimeSpan.FromMinutes(15); // SQS DelaySeconds maximum
    public TimeSpan? MaxRedeliveryDelay => TimeSpan.FromHours(12); // SQS ChangeMessageVisibility maximum
    public TimeSpan? MaxVisibilityTimeout => TimeSpan.FromHours(12); // SQS visibility maximum

    public async Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(messages);

        var items = new List<SendItemResult>(messages.Count);

        // The caller states the destination role, so route without inferring: a topic publishes to SNS, anything else
        // sends to an SQS queue.
        if (options.DestinationRole == DestinationRole.Topic)
        {
            string topicArn = await ResolveTopicArnAsync(destination, ct).ConfigureAwait(false);
            foreach (var message in messages)
            {
                var (body, encoding) = EncodeBody(message);
                var response = await _sns.Value.PublishAsync(new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = body,
                    MessageAttributes = BuildAttributes(message.Headers, encoding, static value => new SnsMessageAttributeValue { DataType = "String", StringValue = value })
                }, ct).ConfigureAwait(false);

                items.Add(new SendItemResult { MessageId = response.MessageId, Success = true });
            }

            return new SendResult { Items = items };
        }

        string queueUrl = await ResolveQueueUrlAsync(destination, ct).ConfigureAwait(false);
        int? delaySeconds = ToDelaySeconds(options.DeliverAt);
        foreach (var message in messages)
        {
            var (body, encoding) = EncodeBody(message);
            var request = new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = body,
                MessageAttributes = BuildAttributes(message.Headers, encoding, static value => new SqsMessageAttributeValue { DataType = "String", StringValue = value })
            };
            if (delaySeconds is { } delay)
                request.DelaySeconds = delay;

            var response = await _sqs.Value.SendMessageAsync(request, ct).ConfigureAwait(false);
            items.Add(new SendItemResult { MessageId = response.MessageId, Success = true });
        }

        return new SendResult { Items = items };
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
    {
        return ReceiveAsync(source, request, _options.DefaultVisibilityTimeout, ct);
    }

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(request);

        string queueUrl = await ResolveQueueUrlAsync(source, ct).ConfigureAwait(false);

        var sqsRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = Math.Clamp(request.MaxMessages <= 0 ? 1 : request.MaxMessages, 1, 10),
            VisibilityTimeout = (int)Math.Clamp(visibility.TotalSeconds, 0, 43200),
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["All"]
        };
        if (request.MaxWaitTime is { } wait)
            sqsRequest.WaitTimeSeconds = (int)Math.Clamp(wait.TotalSeconds, 0, 20);

        var response = await _sqs.Value.ReceiveMessageAsync(sqsRequest, ct).ConfigureAwait(false);
        if (response.Messages is not { Count: > 0 })
            return [];

        var entries = new List<TransportEntry>(response.Messages.Count);
        foreach (var message in response.Messages)
        {
            entries.Add(new TransportEntry
            {
                Id = message.MessageId,
                Destination = source,
                Body = DecodeBody(message.Body, GetAttribute(message.MessageAttributes, EncodingAttributeName)),
                Headers = FromSqsAttributes(message.MessageAttributes),
                DeliveryCount = GetReceiveCount(message),
                Receipt = new Receipt { TransportState = message.ReceiptHandle }
            });
        }

        return entries;
    }

    public async Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        string queueUrl = await ResolveQueueUrlAsync(entry.Destination, ct).ConfigureAwait(false);
        await _sqs.Value.DeleteMessageAsync(queueUrl, GetReceiptHandle(entry), ct).ConfigureAwait(false);
    }

    public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
    {
        return AbandonAsync(entry, TimeSpan.Zero, ct);
    }

    public async Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct)
    {
        ThrowIfDisposed();
        string queueUrl = await ResolveQueueUrlAsync(entry.Destination, ct).ConfigureAwait(false);
        // Returning a message to the queue is a visibility change to the requested delay (0 = immediately visible).
        await _sqs.Value.ChangeMessageVisibilityAsync(queueUrl, GetReceiptHandle(entry), (int)Math.Clamp(redeliveryDelay.TotalSeconds, 0, 43200), ct).ConfigureAwait(false);
    }

    public async Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct)
    {
        ThrowIfDisposed();
        string queueUrl = await ResolveQueueUrlAsync(entry.Destination, ct).ConfigureAwait(false);
        int seconds = (int)Math.Clamp((duration ?? _options.DefaultVisibilityTimeout).TotalSeconds, 0, 43200);
        await _sqs.Value.ChangeMessageVisibilityAsync(queueUrl, GetReceiptHandle(entry), seconds, ct).ConfigureAwait(false);
    }

    public async Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(declarations);

        foreach (var declaration in declarations)
        {
            switch (declaration.Role)
            {
                case DestinationRole.Topic:
                    await ResolveTopicArnAsync(declaration.Name, ct).ConfigureAwait(false);
                    break;
                case DestinationRole.Subscription:
                case DestinationRole.Binding:
                    await EnsureSubscriptionAsync(declaration.Name, declaration.Source, ct).ConfigureAwait(false);
                    break;
                default:
                    await ResolveQueueUrlAsync(declaration.Name, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_roles.TryGetValue(name, out var role) && role == DestinationRole.Topic)
        {
            if (_topicArns.TryRemove(name, out string? arn))
                await _sns.Value.DeleteTopicAsync(arn, ct).ConfigureAwait(false);
        }
        else if (_queueUrls.TryRemove(name, out string? url))
        {
            await _sqs.Value.DeleteQueueAsync(url, ct).ConfigureAwait(false);
        }

        _roles.TryRemove(name, out _);
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_roles.TryGetValue(name, out var role) && role == DestinationRole.Topic)
            return _topicArns.ContainsKey(name);

        try
        {
            await _sqs.Value.GetQueueUrlAsync(ResourceName(name), ct).ConfigureAwait(false);
            return true;
        }
        catch (QueueDoesNotExistException)
        {
            return false;
        }
    }

    public async Task<MessageDestinationStats> GetStatsAsync(string destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        string queueUrl = await ResolveQueueUrlAsync(destination, ct).ConfigureAwait(false);
        var response = await _sqs.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["All"]
        }, ct).ConfigureAwait(false);

        return new MessageDestinationStats
        {
            Queued = response.ApproximateNumberOfMessages,
            Working = response.ApproximateNumberOfMessagesNotVisible
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        if (_sqs.IsValueCreated)
            _sqs.Value.Dispose();
        if (_sns.IsValueCreated)
            _sns.Value.Dispose();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task EnsureSubscriptionAsync(string subscriptionName, string? topicName, CancellationToken ct)
    {
        string queueUrl = await ResolveQueueUrlAsync(subscriptionName, ct).ConfigureAwait(false);
        _roles[subscriptionName] = DestinationRole.Subscription;

        if (String.IsNullOrEmpty(topicName))
            return;

        string topicArn = await ResolveTopicArnAsync(topicName, ct).ConfigureAwait(false);
        string queueArn = await GetQueueArnAsync(queueUrl, ct).ConfigureAwait(false);

        // Allow the topic to deliver to the queue, then subscribe with raw delivery so the SQS body/attributes match a
        // direct SQS send (no SNS envelope).
        await _sqs.Value.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string> { ["Policy"] = BuildQueuePolicy(queueArn, topicArn) }
        }, ct).ConfigureAwait(false);

        await _sns.Value.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true
        }, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveQueueUrlAsync(string name, CancellationToken ct)
    {
        if (_queueUrls.TryGetValue(name, out string? cached))
            return cached;

        string resourceName = ResourceName(name);
        try
        {
            var response = await _sqs.Value.GetQueueUrlAsync(resourceName, ct).ConfigureAwait(false);
            _queueUrls[name] = response.QueueUrl;
            _roles.TryAdd(name, DestinationRole.Queue);
            return response.QueueUrl;
        }
        catch (QueueDoesNotExistException) when (_options.AutoCreateDestinations)
        {
            var response = await _sqs.Value.CreateQueueAsync(new CreateQueueRequest { QueueName = resourceName }, ct).ConfigureAwait(false);
            _queueUrls[name] = response.QueueUrl;
            _roles.TryAdd(name, DestinationRole.Queue);
            return response.QueueUrl;
        }
    }

    private async Task<string> ResolveTopicArnAsync(string name, CancellationToken ct)
    {
        if (_topicArns.TryGetValue(name, out string? cached))
            return cached;

        // CreateTopic is idempotent and returns the ARN of an existing topic with the same name.
        var response = await _sns.Value.CreateTopicAsync(new CreateTopicRequest { Name = ResourceName(name) }, ct).ConfigureAwait(false);
        _topicArns[name] = response.TopicArn;
        _roles[name] = DestinationRole.Topic;
        return response.TopicArn;
    }

    // SQS queue names and SNS topic names allow alphanumerics, hyphens and underscores; the logical destination name
    // already conforms, so we only prepend the configured prefix.
    private string ResourceName(string logicalName) => _options.ResourcePrefix + logicalName;

    private async Task<string> GetQueueArnAsync(string queueUrl, CancellationToken ct)
    {
        var response = await _sqs.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"]
        }, ct).ConfigureAwait(false);
        return response.QueueARN;
    }

    private static string BuildQueuePolicy(string queueArn, string topicArn)
    {
        return JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { Service = "sns.amazonaws.com" },
                    Action = "sqs:SendMessage",
                    Resource = queueArn,
                    Condition = new { ArnEquals = new Dictionary<string, string> { ["aws:SourceArn"] = topicArn } }
                }
            }
        });
    }

    private int? ToDelaySeconds(DateTimeOffset? deliverAt)
    {
        if (deliverAt is not { } at)
            return null;

        double seconds = (at - DateTimeOffset.UtcNow).TotalSeconds;
        if (seconds <= 0)
            return null;

        return (int)Math.Clamp(seconds, 1, 900); // SQS DelaySeconds maximum is 900 (15 minutes)
    }

    private static int GetReceiveCount(SqsMessage message)
    {
        if (message.Attributes is not null && message.Attributes.TryGetValue("ApproximateReceiveCount", out string? value) && Int32.TryParse(value, out int count) && count > 0)
            return count;
        return 1;
    }

    private static string GetReceiptHandle(TransportEntry entry)
    {
        return entry.Receipt.TransportState as string
            ?? throw new ReceiptExpiredException("The transport entry does not carry an SQS receipt handle.");
    }

    // A text body (e.g. JSON, the default) is stored as-is so it is human-readable in the console and avoids base64
    // overhead; anything else is base64-encoded so arbitrary bytes round-trip through SQS/SNS string bodies. The chosen
    // encoding is recorded in a native attribute for the receive side.
    private static (string Body, string Encoding) EncodeBody(TransportMessage message)
    {
        return IsTextContent(message.ContentType)
            ? (Encoding.UTF8.GetString(message.Body.Span), "text")
            : (Convert.ToBase64String(message.Body.Span), "base64");
    }

    private static ReadOnlyMemory<byte> DecodeBody(string body, string? encoding)
    {
        if (String.IsNullOrEmpty(body))
            return ReadOnlyMemory<byte>.Empty;

        return String.Equals(encoding, "text", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(body)
            : Convert.FromBase64String(body);
    }

    private static bool IsTextContent(string? contentType)
    {
        return !String.IsNullOrEmpty(contentType)
            && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, TAttribute> BuildAttributes<TAttribute>(MessageHeaders headers, string encoding, Func<string, TAttribute> stringAttribute)
    {
        var attributes = new Dictionary<string, TAttribute>(StringComparer.Ordinal)
        {
            [HeadersAttributeName] = stringAttribute(EncodeHeaders(headers)),
            [EncodingAttributeName] = stringAttribute(encoding)
        };

        foreach (string name in WellKnownNativeHeaders)
        {
            string? value = headers.GetValueOrDefault(name);
            if (!String.IsNullOrEmpty(value))
                attributes[name] = stringAttribute(value);
        }

        return attributes;
    }

    private static string? GetAttribute(Dictionary<string, SqsMessageAttributeValue>? attributes, string name)
    {
        return attributes is not null && attributes.TryGetValue(name, out var value) ? value.StringValue : null;
    }

    private static MessageHeaders FromSqsAttributes(Dictionary<string, SqsMessageAttributeValue>? attributes)
    {
        if (attributes is null || !attributes.TryGetValue(HeadersAttributeName, out var value) || String.IsNullOrEmpty(value.StringValue))
            return MessageHeaders.Empty;

        return DecodeHeaders(value.StringValue);
    }

    private static string EncodeHeaders(MessageHeaders headers)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
            map[header.Key] = header.Value;
        return JsonSerializer.Serialize(map);
    }

    private static MessageHeaders DecodeHeaders(string json)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return map is null ? MessageHeaders.Empty : MessageHeaders.Create(map);
    }

    private IAmazonSQS CreateSqsClient()
    {
        var config = new AmazonSQSConfig();
        ApplyEndpoint(config);
        return _options.Credentials is { } credentials ? new AmazonSQSClient(credentials, config) : new AmazonSQSClient(config);
    }

    private IAmazonSimpleNotificationService CreateSnsClient()
    {
        var config = new AmazonSimpleNotificationServiceConfig();
        ApplyEndpoint(config);
        return _options.Credentials is { } credentials ? new AmazonSimpleNotificationServiceClient(credentials, config) : new AmazonSimpleNotificationServiceClient(config);
    }

    private void ApplyEndpoint(Amazon.Runtime.ClientConfig config)
    {
        if (!String.IsNullOrEmpty(_options.ServiceUrl))
        {
            config.ServiceURL = _options.ServiceUrl;
            config.AuthenticationRegion = (_options.Region ?? Amazon.RegionEndpoint.USEast1).SystemName;
        }
        else if (_options.Region is { } region)
        {
            config.RegionEndpoint = region;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }
}
