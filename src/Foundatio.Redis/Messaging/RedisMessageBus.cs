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

        public RedisMessageBus(RedisMessageBusOptions options) : base(options) { }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_isSubscribed)
                    return;

                bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribing to topic: {Topic}", _options.Topic);
                await _options.Subscriber.SubscribeAsync(_options.Topic, OnMessageAsync).AnyContext();
                _isSubscribed = true;
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribed to topic: {Topic}", _options.Topic);
            }
        }

        private async void OnMessageAsync(RedisChannel channel, RedisValue value) {
            if (_subscribers.IsEmpty)
                return;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("OnMessage({Channel})", channel);
            MessageBusData message;
            try {
                message = _serializer.Deserialize<MessageBusData>((byte[])value);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "OnMessage({Channel}) Error deserializing messsage: {Message}", channel, ex.Message);
                return;
            }

            await SendMessageToSubscribersAsync(message, _serializer).AnyContext();
        }

        protected override async Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                await AddDelayedMessageAsync(messageType, message, delay.Value).AnyContext();
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Message Publish: {MessageType}", messageType.FullName);
            var data = _serializer.SerializeToBytes(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = _serializer.SerializeToBytes(message)
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
                    _options.Subscriber.Unsubscribe(_options.Topic, OnMessageAsync, CommandFlags.FireAndForget);
                    _isSubscribed = false;
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Unsubscribed from topic {Topic}", _options.Topic);
                }
            }
        }
    }
}
