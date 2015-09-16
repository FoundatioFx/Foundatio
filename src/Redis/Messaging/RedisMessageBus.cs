using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBus : MessageBusBase, IMessageBus {
        private readonly ISubscriber _subscriber;
        private readonly string _topic;
        private readonly ISerializer _serializer;

        public RedisMessageBus(ISubscriber subscriber, string topic = null, ISerializer serializer = null) {
            _subscriber = subscriber;
            _topic = topic ?? "messages";
            _serializer = serializer ?? new JsonNetSerializer();
            Logger.Trace().Message("Subscribing to topic: {0}", _topic).Write();
            _subscriber.Subscribe(_topic, OnMessage);
        }

        private async void OnMessage(RedisChannel channel, RedisValue value) {
            Logger.Trace().Message($"OnMessage: {channel}").Write();
            var message = _serializer.Deserialize<MessageBusData>((string)value);

            Type messageType;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting message body type: {0}", ex.Message).Write();
                return;
            }

            object body = _serializer.Deserialize(message.Data, messageType);
            await SendMessageToSubscribersAsync(messageType, body);
        }

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("Message Publish: {0}", messageType.FullName).Write();
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            var data = _serializer.Serialize(new MessageBusData { Type = messageType.AssemblyQualifiedName, Data = _serializer.SerializeToString(message) });
            return _subscriber.PublishAsync(_topic, data, CommandFlags.FireAndForget);
        }
        
        public override void Dispose() {
            _subscriber.Unsubscribe(_topic);
            base.Dispose();
        }
    }
}
