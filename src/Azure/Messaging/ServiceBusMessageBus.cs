using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Messaging {
    public class ServiceBusMessageBus : MessageBusBase, IMessageBus {
        private readonly string _topicName;
        private readonly ISerializer _serializer;
        private readonly string _subscriptionName;
        private readonly NamespaceManager _namespaceManager;
        private readonly TopicClient _topicClient;
        private readonly SubscriptionClient _subscriptionClient;
        private readonly BlockingCollection<Subscriber> _subscribers = new BlockingCollection<Subscriber>();

        public ServiceBusMessageBus(string connectionString, string topicName, ISerializer serializer = null) {
            _topicName = topicName;
            _serializer = serializer ?? new JsonNetSerializer();
            _subscriptionName = "MessageBus";
            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            if (!_namespaceManager.TopicExists(_topicName))
                _namespaceManager.CreateTopic(_topicName);

            _topicClient = TopicClient.CreateFromConnectionString(connectionString, _topicName);
            if (!_namespaceManager.SubscriptionExists(_topicName, _subscriptionName))
                _namespaceManager.CreateSubscription(_topicName, _subscriptionName);

            _subscriptionClient = SubscriptionClient.CreateFromConnectionString(connectionString, _topicName, _subscriptionName, ReceiveMode.ReceiveAndDelete);
            _subscriptionClient.OnMessage(OnMessage, new OnMessageOptions { AutoComplete = true });
        }

        private void OnMessage(BrokeredMessage brokeredMessage) {
            var message = brokeredMessage.GetBody<MessageBusData>();

            Type messageType = null;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting message body type: {0}", ex.Message).Write();
            }

            object body = _serializer.Deserialize(message.Data, messageType);
            foreach (var subscriber in _subscribers.Where(s => s.Type.IsAssignableFrom(messageType)).ToList()) {
                try {
                    subscriber.Action(body);
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                }
            }
        }

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            var brokeredMessage = new BrokeredMessage(new MessageBusData { Type = messageType.AssemblyQualifiedName, Data = _serializer.SerializeToString(message) });
            
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                brokeredMessage.ScheduledEnqueueTimeUtc = DateTime.UtcNow.Add(delay.Value);
            }

            return _topicClient.SendAsync(brokeredMessage);
        }

        public Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = new CancellationToken()) where T : class {
            _subscribers.Add(new Subscriber {
                Type = typeof(T),
                Action = async m => {
                    if (!(m is T))
                        return;

                    await handler((T)m, cancellationToken).AnyContext();
                }
            }, cancellationToken);

            return TaskHelper.Completed();
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Action<object> Action { get; set; }
        }
    }
}
