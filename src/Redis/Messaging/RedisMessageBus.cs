using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBus : MessageBusBase, IMessageBus {
        private readonly ISubscriber _subscriber;
        private readonly BlockingCollection<Subscriber> _subscribers = new BlockingCollection<Subscriber>();
        private readonly string _topic;
        private readonly ISerializer _serializer;

        public RedisMessageBus(ISubscriber subscriber, string topic = null, ISerializer serializer = null) {
            _subscriber = subscriber;
            _topic = topic ?? "messages";
            _serializer = serializer ?? new JsonNetSerializer();
            Logger.Trace().Message("Subscribing to topic: {0}", _topic).Write();
            _subscriber.Subscribe(_topic, OnMessage);
        }

        private void OnMessage(RedisChannel channel, RedisValue value) {
            Logger.Trace().Message("OnMessage: {0}", channel).Write();
            var message = _serializer.Deserialize<MessageBusData>((string)value);

            Type messageType = null;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting message body type: {0}", ex.Message).Write();
            }

            object body = _serializer.Deserialize(message.Data, messageType);
            var messageTypeSubscribers = _subscribers.Where(s => s.Type.IsAssignableFrom(messageType)).ToList();
            Logger.Trace().Message("Found {0} of {1} subscribers for type: {2}", messageTypeSubscribers.Count, _subscribers.Count, message.Type).Write();
            foreach (var subscriber in messageTypeSubscribers) {
                try {
                    subscriber.Action(body);
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                }
            }
        }

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("Message Publish: {0}", messageType.FullName).Write();
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            var data = _serializer.Serialize(new MessageBusData { Type = messageType.AssemblyQualifiedName, Data = _serializer.SerializeToString(message) });
            return _subscriber.PublishAsync(_topic, data, CommandFlags.FireAndForget);
        }

        public Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            Logger.Trace().Message("Adding subscriber for {0}.", typeof(T).FullName).Write();
            _subscribers.Add(new Subscriber {
                Type = typeof(T),
                Action = m => {
                    if (!(m is T))
                        return;

                    handler((T)m, cancellationToken);
                }
            }, cancellationToken);

            return Task.FromResult(0);
        }

        public override void Dispose() {
            _subscriber.Unsubscribe(_topic);
            base.Dispose();
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Action<object> Action { get; set; }
        }
    }
}
