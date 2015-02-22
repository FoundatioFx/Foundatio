using System;
using System.Collections.Concurrent;
using System.Linq;
using Foundatio.Extensions;
using Foundatio.Messaging;
using NLog.Fluent;
using StackExchange.Redis;

namespace Foundatio.Redis.Messaging {
    public class RedisMessageBus : MessageBusBase, IMessageBus {
        private readonly ISubscriber _subscriber;
        private readonly BlockingCollection<Subscriber> _subscribers = new BlockingCollection<Subscriber>();
        private readonly string _topic;

        public RedisMessageBus(ISubscriber subscriber, string topic = null) {
            _subscriber = subscriber;
            _topic = topic ?? "messages";
            Log.Trace().Message("Subscribing to topic: {0}", _topic).Write();
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
            var messageTypeSubscribers = _subscribers.Where(s => s.Type.IsAssignableFrom(messageType)).ToList();
            foreach (var sub in _subscribers)
                Log.Trace().Message("Sub: {0}", sub.Type).Write();
            Log.Trace().Message("Found {0} of {1} subscribers for type: {2}", messageTypeSubscribers.Count, _subscribers.Count, message.Type).Write();
            foreach (var subscriber in messageTypeSubscribers) {
                try {
                    Log.Trace().Message("Calling subscriber...").Write();
                    subscriber.Action(body);
                    Log.Trace().Message("Done calling subscriber.").Write();
                } catch (Exception ex) {
                    Log.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                }
            }
        }

        public override void Publish(Type messageType, object message, TimeSpan? delay = null) {
            Log.Trace().Message("Message Publish: {0}", messageType.FullName).Write();
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                AddDelayedMessage(messageType, message, delay.Value);
                return;
            }

            _subscriber.Publish(_topic, new MessageBusData { Type = messageType.AssemblyQualifiedName, Data = message.ToJson() }.ToJson());
            Log.Trace().Message("Message Published To: {0}", _topic).Write();
        }

        public void Subscribe<T>(Action<T> handler) where T: class {
            Log.Trace().Message("Adding subscriber for {0}.", typeof(T).FullName).Write();
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
            _subscriber.Unsubscribe(_topic);
            base.Dispose();
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Action<object> Action { get; set; }
        }
    }
}
