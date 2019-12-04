using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBus : MessageBusBase<RedisMessageBusOptions> {
        private readonly AsyncLock _lock = new AsyncLock();
        private bool _isSubscribed;

        public RedisMessageBus(RedisMessageBusOptions options) : base(options) {}

        public RedisMessageBus(Builder<RedisMessageBusOptionsBuilder, RedisMessageBusOptions> config)
            : this(config(new RedisMessageBusOptionsBuilder()).Build()) { }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_isSubscribed)
                    return;

                bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribing to topic: {Topic}", _options.Topic);
                await _options.Subscriber.SubscribeAsync(_options.Topic, OnMessage).AnyContext();
                _isSubscribed = true;
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribed to topic: {Topic}", _options.Topic);
            }
        }

        private void OnMessage(RedisChannel channel, RedisValue value) {
            if (_subscribers.IsEmpty)
                return;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("OnMessage({Channel})", channel);
            IMessage message;
            try {
                var envelope = _serializer.Deserialize<RedisMessageEnvelope>((byte[])value);
                message = new Message(() => DeserializeMessageBody(envelope.Type, envelope.Data)) {
                    Type = envelope.Type,
                    Data = envelope.Data,
                    ClrType = GetMappedMessageType(envelope.Type)
                };
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "OnMessage({Channel}) Error deserializing messsage: {Message}", channel, ex.Message);
                return;
            }

            SendMessageToSubscribers(message);
        }

        protected override async Task PublishImplAsync(string messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            var mappedType = GetMappedMessageType(messageType);
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, delay.Value.TotalMilliseconds);
                await AddDelayedMessageAsync(mappedType, message, delay.Value).AnyContext();
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Message Publish: {MessageType}", messageType);
            var bodyData = SerializeMessageBody(messageType, message);
            byte[] data = _serializer.SerializeToBytes(new RedisMessageEnvelope() {
                Type = messageType,
                Data = bodyData
            });

            await Run.WithRetriesAsync(() => _options.Subscriber.PublishAsync(_options.Topic, data, CommandFlags.FireAndForget), logger: _logger, cancellationToken: cancellationToken).AnyContext();
        }

        public override void Dispose() {
            base.Dispose();

            if (_isSubscribed) {
                using (_lock.Lock()) {
                    if (!_isSubscribed)
                        return;

                    bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Unsubscribing from topic {Topic}", _options.Topic);
                    _options.Subscriber.Unsubscribe(_options.Topic, OnMessage, CommandFlags.FireAndForget);
                    _isSubscribed = false;
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Unsubscribed from topic {Topic}", _options.Topic);
                }
            }
        }
    }

    public class RedisMessageEnvelope {
        public string Type { get; set; }
        public byte[] Data { get; set; }
    }
}
