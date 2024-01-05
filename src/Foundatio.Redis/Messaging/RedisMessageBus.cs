using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging
{
    public class RedisMessageBus : MessageBusBase<RedisMessageBusOptions>
    {
        private readonly AsyncLock _lock = new();
        private bool _isSubscribed;
        private ChannelMessageQueue _channelMessageQueue = null;

        public RedisMessageBus(RedisMessageBusOptions options) : base(options) { }

        public RedisMessageBus(Builder<RedisMessageBusOptionsBuilder, RedisMessageBusOptions> config)
            : this(config(new RedisMessageBusOptionsBuilder()).Build()) { }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken)
        {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync().AnyContext())
            {
                if (_isSubscribed)
                    return;

                bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribing to topic: {Topic}", _options.Topic);
                _channelMessageQueue = await _options.Subscriber.SubscribeAsync(_options.Topic).AnyContext();
                _channelMessageQueue.OnMessage(OnMessage);
                _isSubscribed = true;
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribed to topic: {Topic}", _options.Topic);
            }
        }

        private async Task OnMessage(ChannelMessage channelMessage)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("OnMessage({Channel})", channelMessage.Channel);

            if (_subscribers.IsEmpty || !channelMessage.Message.HasValue)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
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
                _logger.LogWarning(ex, "OnMessage({Channel}) Error deserializing message: {Message}", channelMessage.Channel, ex.Message);
                return;
            }

            await SendMessageToSubscribersAsync(message).AnyContext();
        }

        protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
        {
            var mappedType = GetMappedMessageType(messageType);
            if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
                await AddDelayedMessageAsync(mappedType, message, options.DeliveryDelay.Value).AnyContext();
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Message Publish: {MessageType}", messageType);
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

            await Run.WithRetriesAsync(() => _options.Subscriber.PublishAsync(_options.Topic, data, CommandFlags.FireAndForget), logger: _logger, cancellationToken: cancellationToken).AnyContext();
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

                    bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Unsubscribing from topic {Topic}", _options.Topic);
                    _channelMessageQueue?.Unsubscribe(CommandFlags.FireAndForget);
                    _channelMessageQueue = null;
                    _isSubscribed = false;
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Unsubscribed from topic {Topic}", _options.Topic);
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
}
