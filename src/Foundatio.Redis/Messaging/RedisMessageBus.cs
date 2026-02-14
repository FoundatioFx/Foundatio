using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Redis;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging;

public class RedisMessageBus : MessageBusBase<RedisMessageBusOptions>
{
    private readonly AsyncLock _lock = new();
    private bool _isSubscribed;
    private ChannelMessageQueue _channelMessageQueue;
    private RedisChannel? _channel;

    public RedisMessageBus(RedisMessageBusOptions options) : base(options)
    {
    }

    public RedisMessageBus(Builder<RedisMessageBusOptionsBuilder, RedisMessageBusOptions> config)
        : this(config(new RedisMessageBusOptionsBuilder()).Build())
    {
    }

    /// <summary>
    /// Gets the Redis channel for pub/sub. In Redis Cluster mode, uses sharded pub/sub
    /// (SPUBLISH/SSUBSCRIBE) to avoid duplicate message delivery caused by cluster-wide
    /// broadcast. Regular PUBLISH in a cluster broadcasts to all nodes, and StackExchange.Redis
    /// spreads Literal subscriptions across nodes, which can cause subscribers to receive
    /// the same message multiple times from different primaries. Sharded pub/sub routes all
    /// operations for a given channel through a single shard, preventing per-primary duplicate
    /// delivery while preserving full fanout to all subscribers on that shard. This does not
    /// provide end-to-end exactly-once semantics; callers must remain tolerant of lost or
    /// duplicated messages across disconnects and retries.
    /// Falls back to standard PUBLISH/SUBSCRIBE for standalone, sentinel, and proxy deployments.
    /// See: https://redis.io/docs/latest/commands/spublish/
    /// See: https://redis.io/docs/latest/commands/ssubscribe/
    /// </summary>
    private RedisChannel Channel => _channel ??= _options.Subscriber.Multiplexer.IsCluster()
        ? RedisChannel.Sharded(_options.Topic)
        : RedisChannel.Literal(_options.Topic);

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_isSubscribed)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_isSubscribed)
                return;

            _logger.LogTrace("Subscribing to topic: {Topic}", _options.Topic);
            _channelMessageQueue = await _options.Subscriber.SubscribeAsync(Channel).AnyContext();
            _channelMessageQueue.OnMessage(OnMessage);
            _isSubscribed = true;
            _logger.LogTrace("Subscribed to topic: {Topic}", _options.Topic);
        }
    }

    private async Task OnMessage(ChannelMessage channelMessage)
    {
        using var _ = _logger.BeginScope(s => s
            .Property("Channel", channelMessage.Channel.ToString()));

        _logger.LogTrace("OnMessage({Channel})", channelMessage.Channel);
        if (_subscribers.IsEmpty || !channelMessage.Message.HasValue)
        {
            _logger.LogTrace("No subscribers ({Channel})", channelMessage.Channel);
            return;
        }

        IMessage message;
        try
        {
            var envelope = _serializer.Deserialize<RedisMessageEnvelope>((byte[])channelMessage.Message);
            message = new Message(envelope.Data, DeserializeMessageBody)
            {
                Type = envelope.Type,
                ClrType = GetMappedMessageType(envelope.Type),
                CorrelationId = envelope.CorrelationId,
                UniqueId = envelope.UniqueId
            };

            foreach (var property in envelope.Properties)
                message.Properties.Add(property.Key, property.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnMessage({Channel}) Error deserializing message: {Message}", channelMessage.Channel, ex.Message);
            return;
        }

        try
        {
            await SendMessageToSubscribersAsync(message).AnyContext();
        }
        catch (MessageBusException)
        {
            // SendMessageToSubscribersAsync already logged the error
            // Redis pub/sub has no acknowledgment mechanism - message is lost
        }
        catch (Exception ex)
        {
            // Catch any other unexpected exceptions for defensive purposes
            _logger.LogError(ex, "OnMessage({Channel}) Error in subscriber: {Message}", channelMessage.Channel, ex.Message);
        }
    }

    protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
    {
        var mappedType = GetMappedMessageType(messageType);
        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
            SendDelayedMessage(mappedType, message, options);
            return;
        }

        _logger.LogTrace("Message Publish: {MessageType}", messageType);
        byte[] bodyData = SerializeMessageBody(messageType, message);
        byte[] data = _serializer.SerializeToBytes(new RedisMessageEnvelope
        {
            Type = messageType,
            Data = bodyData,
            CorrelationId = options.CorrelationId,
            UniqueId = options.UniqueId,
            Properties = options.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        });

        // TODO: Use ILockProvider to lock on UniqueId to ensure it doesn't get duplicated
        // Wrap only the transport call in resilience policy
        await _resiliencePolicy.ExecuteAsync(async _ =>
            await _options.Subscriber.PublishAsync(Channel, data, CommandFlags.FireAndForget),
            cancellationToken).AnyContext();
    }

    public override void Dispose()
    {
        base.Dispose();

        if (_isSubscribed)
        {
            using (_lock.Lock())
            {
                if (!_isSubscribed)
                    return;

                _logger.LogTrace("Unsubscribing from topic {Topic}", _options.Topic);
                try
                {
                    _channelMessageQueue?.Unsubscribe(CommandFlags.FireAndForget);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unsubscribing from topic {Topic}: {Message}", _options.Topic, ex.Message);
                }
                _channelMessageQueue = null;
                _isSubscribed = false;
                _logger.LogTrace("Unsubscribed from topic {Topic}", _options.Topic);
            }
        }
    }
}

public class RedisMessageEnvelope
{
    public string UniqueId { get; set; }
    public string CorrelationId { get; set; }
    public string Type { get; set; }
    public byte[] Data { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}
