using System;
using System.Collections.Concurrent;
using System.Linq;
using Foundatio.Extensions;
using NLog.Fluent;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBus : MessageBusBase, IMessageBus {
        private readonly ISubscriber _subscriber;
        private readonly BlockingCollection<Subscriber> _subscribers = new BlockingCollection<Subscriber>();
        private readonly string _topic;

        public RedisMessageBus(ISubscriber subscriber, string topic = null) {
            _subscriber = subscriber;
            _topic = topic ?? "messages";
            _subscriber.Subscribe(_topic, OnMessage);
        }

        private void OnMessage(RedisChannel channel, RedisValue value) {
            Log.Trace().Message("OnMessage: {0}", channel).Write();
            var message = ((string)value).FromJson<MessageBusData>();
            Log.Trace().Message("Deserialized Message: {0}", message.Type).Write();

            Type messageType = null;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                Log.Error().Exception(ex).Message("Error getting message body type: {0}", ex.Message).Write();
            }

            object body = message.Data.FromJson(messageType);
            Log.Trace().Message("Deserialized Message Data: {0}", message.Type).Write();
            var subscribers = _subscribers.Where(s => s.Type.IsAssignableFrom(messageType)).ToList();
            Log.Trace().Message("Found {0} subscribers for type: {1}", subscribers.Count, message.Type).Write();
            foreach (var subscriber in subscribers) {
                try {
                    subscriber.Action(body);
                } catch (Exception ex) {
                    Log.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                }
            }
        }

        public override void Publish(Type messageType, object message, TimeSpan? delay = null) {
            Log.Trace().Message("Message Publish: {0}", messageType.FullName).Write();
            base.Publish(messageType, message, delay);

            // TODO: Implement more robust delayed messages on Redis that have better deliverability guarantees.
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return;

            _subscriber.Publish(_topic, new MessageBusData { Type = messageType.AssemblyQualifiedName, Data = message.ToJson() }.ToJson());
            Log.Trace().Message("Message Published To: {0}", _topic).Write();
        }

        public void Subscribe<T>(Action<T> handler) where T: class {
            _subscribers.Add(new Subscriber {
                Type = typeof(T),
                Action = m => {
                    if (!(m is T))
                        return;

                    handler(m as T);
                }
            });
        }

        public override void Dispose() {
            _subscriber.UnsubscribeAll();
            base.Dispose();
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Action<object> Action { get; set; }
        }
    }
}
