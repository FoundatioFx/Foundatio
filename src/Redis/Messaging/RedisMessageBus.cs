using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBus : MessageBusBase, IMessageBus {
        private readonly ISubscriber _subscriber;
        private readonly string _topic;
        private readonly ISerializer _serializer;
        private static readonly object _lockObject = new object();
        private bool _isSubscribed;

        public RedisMessageBus(ISubscriber subscriber, string topic = null, ISerializer serializer = null) {
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

                _isSubscribed = true;
                Logger.Trace().Message("Subscribing to topic: {0}", _topic).Write();
                _subscriber.Subscribe(_topic, OnMessage);
            }
        }

        private async void OnMessage(RedisChannel channel, RedisValue value) {
#if DEBUG
            Logger.Trace().Message($"OnMessage: {channel}").Write();
#endif
            var message = await _serializer.DeserializeAsync<MessageBusData>((string)value).AnyContext();

            Type messageType;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting message body type: {0}", ex.Message).Write();
                return;
            }

            object body = await _serializer.DeserializeAsync(message.Data, messageType).AnyContext();
            await SendMessageToSubscribersAsync(messageType, body).AnyContext();
        }

        public override async Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null)
                return;

#if DEBUG
            Logger.Trace().Message($"Message Publish: {messageType.FullName}").Write();
#endif
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                await AddDelayedMessageAsync(messageType, message, delay.Value).AnyContext();
                return;
            }

            var data = await _serializer.SerializeAsync(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext()
            }).AnyContext();

            await _subscriber.PublishAsync(_topic, data, CommandFlags.FireAndForget).AnyContext();
        }

        public override void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) {
            EnsureTopicSubscription();
            base.Subscribe(handler, cancellationToken);
        }

        public override void Dispose() {
            _subscriber.Unsubscribe(_topic);
            base.Dispose();
        }
    }
}
