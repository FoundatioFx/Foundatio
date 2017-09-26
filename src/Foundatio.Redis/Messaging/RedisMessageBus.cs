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

        [Obsolete("Use the options overload")]
        public RedisMessageBus(ISubscriber subscriber, string topic = null, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : this(new RedisMessageBusOptions { Subscriber = subscriber, Topic = topic, Serializer = serializer, LoggerFactory = loggerFactory }) { }

        public RedisMessageBus(RedisMessageBusOptions options) : base(options) { }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_isSubscribed)
                    return;

                _logger.LogTrace("Subscribing to topic: {0}", _options.Topic);
                await _options.Subscriber.SubscribeAsync(_options.Topic, OnMessage).AnyContext();
                _isSubscribed = true;
                _logger.LogTrace("Subscribed to topic: {0}", _options.Topic);
            }
        }

        private async void OnMessage(RedisChannel channel, RedisValue value) {
            if (_subscribers.IsEmpty)
                return;

            _logger.LogTrace("OnMessage({channel})", channel);
            MessageBusData message;
            try {
                message = await _serializer.DeserializeAsync<MessageBusData>((byte[])value).AnyContext();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "OnMessage({0}) Error deserializing messsage: {1}", channel, ex.Message);
                return;
            }

            await SendMessageToSubscribersAsync(message, _serializer).AnyContext();
        }

        protected override async Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                _logger.LogTrace("Schedule delayed message: {messageType} ({delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                await AddDelayedMessageAsync(messageType, message, delay.Value).AnyContext();
                return;
            }

            _logger.LogTrace("Message Publish: {messageType}", messageType.FullName);
            var data = await _serializer.SerializeAsync(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext()
            }).AnyContext();

            await Run.WithRetriesAsync(() => _options.Subscriber.PublishAsync(_options.Topic, data, CommandFlags.FireAndForget), logger: _logger, cancellationToken: cancellationToken).AnyContext();
        }

        public override void Dispose() {
            base.Dispose();

            if (_isSubscribed) {
                using (_lock.Lock()) {
                    if (!_isSubscribed)
                        return;

                    _logger.LogTrace("Unsubscribing from topic {0}", _options.Topic);
                    _options.Subscriber.Unsubscribe(_options.Topic, OnMessage, CommandFlags.FireAndForget);
                    _isSubscribed = false;
                    _logger.LogTrace("Unsubscribed from topic {0}", _options.Topic);
                }
            }
        }
    }
}
