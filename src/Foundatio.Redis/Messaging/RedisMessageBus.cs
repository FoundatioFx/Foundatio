using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBus : MessageBusBase, IMessageBus {
        private readonly ISubscriber _subscriber;
        private readonly string _topic;
        private readonly ISerializer _serializer;
        private static readonly object _lockObject = new object();
        private bool _isSubscribed;

        public RedisMessageBus(ISubscriber subscriber, string topic = null, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _subscriber = subscriber;
            _topic = topic ?? "messages";
            _serializer = serializer ?? new JsonNetSerializer();
        }

        private void EnsureTopicSubscription() {
            if (_isSubscribed)
                return;

            lock (_lockObject) {
                if (_isSubscribed)
                    return;

                _logger.Trace("Subscribing to topic: {0}", _topic);
                _subscriber.Subscribe(_topic, OnMessage);
                _isSubscribed = true;
            }
        }

        private async void OnMessage(RedisChannel channel, RedisValue value) {
            if (_subscribers.IsEmpty)
                return;

            _logger.Trace("OnMessage({channel})", channel);
            MessageBusData message;
            try {
                message = await _serializer.DeserializeAsync<MessageBusData>((string)value).AnyContext();
            } catch (Exception ex) {
                _logger.Error(ex, "OnMessage({0}) Error while deserializing messsage: {1}", channel, ex.Message);
                return;
            }

            await SendMessageToSubscribersAsync(message, _serializer).AnyContext();
        }

        public override async Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null)
                return;

            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                _logger.Trace("Schedule delayed message: {messageType} ({delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                await AddDelayedMessageAsync(messageType, message, delay.Value).AnyContext();
                return;
            }

            _logger.Trace("Message Publish: {messageType}", messageType.FullName);
            var data = await _serializer.SerializeAsync(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext()
            }).AnyContext();

            await Run.WithRetriesAsync(() => _subscriber.PublishAsync(_topic, data, CommandFlags.FireAndForget), logger: _logger, cancellationToken: cancellationToken).AnyContext();
        }

        public override void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) {
            EnsureTopicSubscription();
            base.Subscribe(handler, cancellationToken);
        }

        public override void Dispose() {
            _logger.Trace("MessageBus dispose");
            base.Dispose();

            if (_isSubscribed) {
                lock (_lockObject) {
                    if (!_isSubscribed)
                        return;

                    _logger.Trace("Unsubscribing from topic {0}", _topic);
                    _subscriber.Unsubscribe(_topic, OnMessage, CommandFlags.FireAndForget);
                    _isSubscribed = false;
                }
            }
        }
    }
}
