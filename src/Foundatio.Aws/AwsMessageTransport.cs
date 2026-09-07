using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using Amazon.SQS.Model;
using Amazon.SQS;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleNotificationService;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessage = Amazon.SQS.Model.Message;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace Foundatio.Messaging;

/// <summary>
/// An <see cref="IMessageTransport"/> over AWS SQS (queues + competing-consumer subscriptions) and SNS (topics). This
/// is a temporary in-repo provider used to validate the redesigned transport contract against a real broker. Queue and
/// subscription destinations are SQS queues; topic destinations are SNS topics fanned out to SQS subscription queues.
/// </summary>
/// <remarks>
/// Capability mapping: pull receive (SQS long poll), visibility timeout, redelivery delay (ChangeMessageVisibility,
/// 12h cap), delayed delivery on queues only (SQS DelaySeconds, 15-minute cap — SNS topics have no native delay, so
/// delayed publishes route through the runtime-store fallback), provisioning, and stats. SQS has no per-message
/// priority, per-message TTL, or push delivery, and no transport-native dead-letter that the core controls the timing
/// of, so those capabilities are intentionally not implemented (the core owns retry/dead-lettering).
/// </remarks>
public sealed partial class AwsMessageTransport : IMessageTransport, ISupportsPull, ISupportsVisibilityTimeout,
    ISupportsLockRenewal, ISupportsRedeliveryDelay, ISupportsProvisioning, ISupportsStats, ITransportInfo
{
    private const string EnvelopeAttributeName = "fnd.envelope";
    private const string HeadersAttributeName = "fnd.headers";
    private const string EncodingAttributeName = "fnd.encoding";
    private const string MessageIdAttributeName = "fnd.id";
    private const string ContentTypeAttributeName = "fnd.content_type";

    private static readonly IReadOnlySet<DestinationRole> _supportedRoles =
        new HashSet<DestinationRole> { DestinationRole.Queue, DestinationRole.Topic, DestinationRole.Subscription, DestinationRole.Binding };

    private readonly AwsMessageTransportOptions _options;
    private readonly string[] _nativeMessageHeaders;
    private readonly Lazy<IAmazonSQS> _sqs;
    private readonly Lazy<IAmazonSimpleNotificationService> _sns;
    private readonly ConcurrentDictionary<string, string> _queueUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _topicArns = new(StringComparer.Ordinal);
    private int _isDisposed;
    private readonly bool _ownsClients = true;

    public AwsMessageTransport(AwsMessageTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _nativeMessageHeaders = options.NativeMessageHeaders.ToArray();
        _sqs = new Lazy<IAmazonSQS>(CreateSqsClient);
        _sns = new Lazy<IAmazonSimpleNotificationService>(CreateSnsClient);
    }

    /// <summary>Uses caller-owned SDK clients, allowing shared connection configuration and deterministic tests.</summary>
    public AwsMessageTransport(AwsMessageTransportOptions options, IAmazonSQS sqs, IAmazonSimpleNotificationService sns) : this(options)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(sns);
        _sqs = new Lazy<IAmazonSQS>(() => sqs);
        _sns = new Lazy<IAmazonSimpleNotificationService>(() => sns);
        _ownsClients = false;
    }

    public AwsMessageTransport(string connectionString) : this(AwsMessageTransportOptions.FromConnectionString(connectionString)) { }

    // Capabilities differ by role: SQS queues take a native DelaySeconds (15-minute cap), SNS topics have no native
    // delay at all — a delayed publish must route through the runtime-store fallback, never silently drop the delay.
    // SQS accepts up to 1 MiB; SNS accepts up to 256 KiB, including message attributes.
    private static readonly TransportCapabilities _queueCapabilities = new()
    {
        DelayedDelivery = true,
        MaxDeliveryDelay = TimeSpan.FromMinutes(15), // SQS DelaySeconds maximum
        MaxMessageBytes = 1048576,
        MaxBatchSize = 10,
        MaxReceiveBatchSize = 10,
        MaxConcurrentReceives = 4,
        ReceiveBatchDelay = TimeSpan.FromMilliseconds(1)
    };

    private static readonly TransportCapabilities _topicCapabilities = new()
    {
        MaxMessageBytes = 262144,
        MaxBatchSize = 10
    };

    public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
    public IReadOnlySet<DestinationRole> SupportedRoles => _supportedRoles;

    public TransportCapabilities GetCapabilities(DestinationAddress destination) =>
        destination.Role == DestinationRole.Topic ? _topicCapabilities : _queueCapabilities;

    public TimeSpan? MaxRedeliveryDelay => TimeSpan.FromHours(12); // SQS ChangeMessageVisibility maximum
    public TimeSpan? MaxVisibilityTimeout => TimeSpan.FromHours(12); // SQS visibility maximum

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct)
    {
        return ReceiveAsync(source, request, _options.DefaultVisibilityTimeout, ct);
    }

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        string queueUrl = await ResolveQueueUrlAsync(source, ct).ConfigureAwait(false);

        var sqsRequest = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = Math.Clamp(request.MaxMessages <= 0 ? 1 : request.MaxMessages, 1, 10),
            VisibilityTimeout = (int)Math.Clamp(visibility.TotalSeconds, 0, 43200),
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["ApproximateReceiveCount"]
        };
        if (request.MaxWaitTime is { } wait)
            sqsRequest.WaitTimeSeconds = (int)Math.Clamp(wait.TotalSeconds, 0, 20);

        var receiveStarted = DateTimeOffset.UtcNow;
        ReceiveMessageResponse response;
        try { response = await _sqs.Value.ReceiveMessageAsync(sqsRequest, ct).ConfigureAwait(false); }
        catch (QueueDoesNotExistException ex)
        {
            _queueUrls.TryRemove(source.Key, out _);
            throw new MessageDestinationNotFoundException(source, ex);
        }
        if (response.Messages is not { Count: > 0 })
            return [];

        if (_options.EnableBatching)
            GetDeleteBatcher(queueUrl).ObserveBatchSize(sqsRequest.MaxNumberOfMessages.GetValueOrDefault(1));

        var entries = new List<TransportEntry>(response.Messages.Count);
        foreach (var message in response.Messages)
        {
            ReadOnlyMemory<byte> body;
            MessageHeaders headers;
            string? applicationMessageId = GetAttribute(message.MessageAttributes, MessageIdAttributeName);
            string? contentType = GetAttribute(message.MessageAttributes, ContentTypeAttributeName);
            Exception? envelopeError = null;
            try
            {
                string encodedBody = message.Body ?? throw new FormatException("Missing message body.");
                if (GetAttribute(message.MessageAttributes, EnvelopeAttributeName) is { } envelopeJson)
                {
                    var envelope = JsonSerializer.Deserialize<AwsEnvelope>(envelopeJson);
                    if (envelope is null || envelope.Version != 1 || envelope.Encoding is not ("text" or "base64") || envelope.Headers is null)
                        throw new FormatException("Invalid or unsupported Foundatio AWS envelope.");
                    body = DecodeBody(encodedBody, envelope.Encoding);
                    headers = MessageHeaders.Create(envelope.Headers);
                    applicationMessageId = envelope.MessageId;
                    contentType = envelope.ContentType;
                }
                else
                {
                    body = DecodeBody(encodedBody, GetAttribute(message.MessageAttributes, EncodingAttributeName));
                    headers = FromSqsAttributes(message.MessageAttributes);
                }
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                envelopeError = ex;
                body = Encoding.UTF8.GetBytes(message.Body ?? "");
                headers = MessageHeaders.Create(new Dictionary<string, string>
                {
                    ["transport.raw.attributes"] = JsonSerializer.Serialize(message.MessageAttributes)
                });
            }
            entries.Add(new TransportEntry
            {
                Id = message.MessageId,
                ApplicationMessageId = applicationMessageId,
                ContentType = contentType,
                Destination = source,
                LockExpiresUtc = receiveStarted.AddSeconds(sqsRequest.VisibilityTimeout.GetValueOrDefault()),
                Body = body,
                Headers = headers,
                EnvelopeError = envelopeError,
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
        string receipt = GetReceiptHandle(entry);
        if (!_options.EnableBatching)
        {
            await _sqs.Value.DeleteMessageAsync(queueUrl, receipt, ct).ConfigureAwait(false);
            return;
        }
        var error = await GetDeleteBatcher(queueUrl).ExecuteAsync(receipt, ct).ConfigureAwait(false);
        if (error is not null)
            throw error;
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
            if (declaration.AutoDeleteAfter is not null)
                throw new NotSupportedException("SQS/SNS do not provide expiring subscription resources. Use an explicitly named durable subscription.");
            switch (declaration.Address.Role)
            {
                case DestinationRole.Topic:
                    await ResolveTopicArnAsync(declaration.Address.Name, allowCreate: true, ct).ConfigureAwait(false);
                    break;
                case DestinationRole.Subscription:
                case DestinationRole.Binding:
                    await EnsureSubscriptionAsync(declaration.Address, ct).ConfigureAwait(false);
                    break;
                default:
                    await ResolveQueueUrlAsync(declaration.Address, allowCreate: true, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    public async Task DeleteAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Role == DestinationRole.Topic)
        {
            string? arn = await FindTopicArnAsync(destination.Name, ct).ConfigureAwait(false);
            if (arn is not null)
                await _sns.Value.DeleteTopicAsync(arn, ct).ConfigureAwait(false);
            _topicArns.TryRemove(destination.Name, out _);
            return;
        }

        if (destination.Topic is { Length: > 0 } topic)
        {
            string? topicArn = await FindTopicArnAsync(topic, ct).ConfigureAwait(false);
            if (topicArn is not null)
            {
                string queueArn = topicArn[..topicArn.LastIndexOf(':')].Replace(":sns:", ":sqs:", StringComparison.Ordinal) + ":" + ResourceName(destination.Key);
                string? subscriptionArn = await FindSubscriptionArnAsync(topicArn, queueArn, ct).ConfigureAwait(false);
                if (subscriptionArn is not null)
                    await _sns.Value.UnsubscribeAsync(subscriptionArn, ct).ConfigureAwait(false);
            }
        }
        try
        {
            var response = await _sqs.Value.GetQueueUrlAsync(ResourceName(destination.Key), ct).ConfigureAwait(false);
            await _sqs.Value.DeleteQueueAsync(response.QueueUrl, ct).ConfigureAwait(false);
        }
        catch (QueueDoesNotExistException)
        {
        }
        _queueUrls.TryRemove(destination.Key, out _);
    }

    public async Task<bool> ExistsAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Role == DestinationRole.Topic)
            return await FindTopicArnAsync(destination.Name, ct).ConfigureAwait(false) is not null;
        try
        {
            var queue = await _sqs.Value.GetQueueUrlAsync(ResourceName(destination.Key), ct).ConfigureAwait(false);
            if (destination.Topic is not { Length: > 0 } topic)
                return true;
            string? topicArn = await FindTopicArnAsync(topic, ct).ConfigureAwait(false);
            if (topicArn is null)
                return false;
            string queueArn = await GetQueueArnAsync(queue.QueueUrl, ct).ConfigureAwait(false);
            return await FindSubscriptionArnAsync(topicArn, queueArn, ct).ConfigureAwait(false) is not null;
        }
        catch (QueueDoesNotExistException)
        {
            return false;
        }
    }

    private async Task<string?> FindTopicArnAsync(string name, CancellationToken ct)
    {
        string resourceName = ResourceName(name);
        string? nextToken = null;
        do
        {
            var page = await _sns.Value.ListTopicsAsync(new ListTopicsRequest { NextToken = nextToken }, ct).ConfigureAwait(false);
            foreach (var topic in page.Topics ?? [])
            {
                if (topic.TopicArn.EndsWith(":" + resourceName, StringComparison.Ordinal))
                    return topic.TopicArn;
            }
            nextToken = page.NextToken;
        } while (!String.IsNullOrEmpty(nextToken));
        return null;
    }

    private async Task<string?> FindSubscriptionArnAsync(string topicArn, string queueArn, CancellationToken ct)
    {
        string? nextToken = null;
        do
        {
            var page = await _sns.Value.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = nextToken }, ct).ConfigureAwait(false);
            foreach (var subscription in page.Subscriptions ?? [])
            {
                if (subscription.Protocol == "sqs" && subscription.Endpoint == queueArn)
                    return subscription.SubscriptionArn;
            }
            nextToken = page.NextToken;
        } while (!String.IsNullOrEmpty(nextToken));
        return null;
    }

    public async Task<MessageDestinationStats> GetStatsAsync(DestinationAddress destination, CancellationToken ct)
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

        await DisposeBatchersAsync().ConfigureAwait(false);
        if (_ownsClients && _sqs.IsValueCreated)
            _sqs.Value.Dispose();
        if (_ownsClients && _sns.IsValueCreated)
            _sns.Value.Dispose();
    }

    private async Task EnsureSubscriptionAsync(DestinationAddress address, CancellationToken ct)
    {
        string queueUrl = await ResolveQueueUrlAsync(address, allowCreate: true, ct).ConfigureAwait(false);

        if (String.IsNullOrEmpty(address.Topic))
            return;

        string topicArn = await ResolveTopicArnAsync(address.Topic, allowCreate: true, ct).ConfigureAwait(false);
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

    // Queue and subscription destinations are both backed by an SQS queue whose logical name is the address key
    // (Name for queues, "topic/subscription" for subscriptions), so provisioning and every runtime path resolve the
    // same physical queue from the same address.
    private Task<string> ResolveQueueUrlAsync(DestinationAddress address, CancellationToken ct) =>
        ResolveQueueUrlAsync(address, allowCreate: false, ct);

    private async Task<string> ResolveQueueUrlAsync(DestinationAddress address, bool allowCreate, CancellationToken ct)
    {
        string key = address.Key;
        if (_queueUrls.TryGetValue(key, out string? cached))
            return cached;

        string resourceName = ResourceName(key);
        try
        {
            var response = await _sqs.Value.GetQueueUrlAsync(resourceName, ct).ConfigureAwait(false);
            _queueUrls[key] = response.QueueUrl;
            return response.QueueUrl;
        }
        catch (QueueDoesNotExistException) when (allowCreate)
        {
            var response = await _sqs.Value.CreateQueueAsync(new CreateQueueRequest { QueueName = resourceName }, ct).ConfigureAwait(false);
            _queueUrls[key] = response.QueueUrl;
            return response.QueueUrl;
        }
    }

    // Sending and receiving resolve existing resources; explicit provisioning via EnsureAsync
    // creates missing resources according to the caller's topology policy.
    private Task<string> ResolveTopicArnAsync(string name, CancellationToken ct) =>
        ResolveTopicArnAsync(name, allowCreate: false, ct);

    private async Task<string> ResolveTopicArnAsync(string name, bool allowCreate, CancellationToken ct)
    {
        if (_topicArns.TryGetValue(name, out string? cached))
            return cached;

        if (allowCreate)
        {
            // CreateTopic is idempotent and returns the ARN of an existing topic with the same name.
            var response = await _sns.Value.CreateTopicAsync(new CreateTopicRequest { Name = ResourceName(name) }, ct).ConfigureAwait(false);
            _topicArns[name] = response.TopicArn;
            return response.TopicArn;
        }

        // Auto-create is disabled (locked-down broker): look the topic up instead of creating it, and fail loudly when
        // it has not been provisioned out of band.
        var existing = await FindTopicArnAsync(name, ct).ConfigureAwait(false);
        if (existing is null)
            throw new InvalidOperationException($"SNS topic \"{ResourceName(name)}\" does not exist and implicit creation is disabled. Provision it with EnsureAsync or through the message bus topology policy.");

        _topicArns[name] = existing;
        return existing;
    }

    // SQS queue / SNS topic names allow only [A-Za-z0-9_-] (max 80 chars). Most logical names already conform, but a
    // subscription's key (see DestinationAddress.Key) is the opaque "topic/subscription" form which contains '/'.
    // Encode any illegal name deterministically and collision-free — sanitize, then append a short stable hash of the
    // original — so EnsureAsync/ReceiveAsync/CompleteAsync all resolve the same queue from the same logical name.
    // Legal names are returned unchanged (no behavior change for plain queues/topics).
    private string ResourceName(string logicalName) => EncodeResourceName(_options.ResourcePrefix, logicalName);

    private static string EncodeResourceName(string prefix, string logicalName)
    {
        string candidate = prefix + logicalName;
        if (IsResourceNameLegal(candidate))
            return candidate;

        string suffix = "-" + StableHash(candidate);
        string sanitized = SanitizeResourceName(candidate);
        if (sanitized.Length > 80 - suffix.Length)
            sanitized = sanitized[..(80 - suffix.Length)];
        return sanitized + suffix;
    }

    private static bool IsResourceNameLegal(string name)
    {
        if (name.Length is 0 or > 80)
            return false;
        foreach (char c in name)
        {
            if (!(Char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return false;
        }

        return true;
    }

    private static string SanitizeResourceName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(Char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return builder.ToString();
    }

    private static string StableHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant(); // 8 hex chars
    }

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

    private PreparedMessage PrepareMessage(int index, TransportMessage message, DateTimeOffset? deliverAt)
    {
        var (body, encoding) = EncodeBody(message);
        var headers = message.Headers;
        string envelope = JsonSerializer.Serialize(new AwsEnvelope(1, encoding, message.MessageId, message.ContentType, headers));
        int bytes = checked(Encoding.UTF8.GetByteCount(body) + AttributeBytes(EnvelopeAttributeName, envelope));
        foreach (string name in _nativeMessageHeaders)
        {
            string? value = headers.GetValueOrDefault(name);
            if (!String.IsNullOrEmpty(value))
                bytes = checked(bytes + AttributeBytes(name, value));
        }
        return new PreparedMessage(index, body, envelope, headers, bytes, deliverAt);

        static int AttributeBytes(string name, string value) => checked(Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value) + 6);
    }

    private Dictionary<string, TAttribute> BuildAttributes<TAttribute>(PreparedMessage message, Func<string, TAttribute> createAttribute)
    {
        var attributes = new Dictionary<string, TAttribute>(_nativeMessageHeaders.Length + 1, StringComparer.Ordinal)
        {
            [EnvelopeAttributeName] = createAttribute(message.Envelope)
        };
        foreach (string name in _nativeMessageHeaders)
        {
            string? value = message.Headers.GetValueOrDefault(name);
            if (!String.IsNullOrEmpty(value))
                attributes[name] = createAttribute(value);
        }
        return attributes;
    }

    private static string? GetAttribute(Dictionary<string, SqsMessageAttributeValue>? attributes, string name)
    {
        return attributes is not null && attributes.TryGetValue(name, out var value) ? value.StringValue : null;
    }

    private sealed record AwsEnvelope(int Version, string Encoding, string? MessageId, string? ContentType, IReadOnlyDictionary<string, string>? Headers);

    private static MessageHeaders FromSqsAttributes(Dictionary<string, SqsMessageAttributeValue>? attributes)
    {
        if (attributes is null || !attributes.TryGetValue(HeadersAttributeName, out var value) || String.IsNullOrEmpty(value.StringValue))
            return MessageHeaders.Empty;

        return MessageHeaders.DeserializeFromJson(value.StringValue);
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
