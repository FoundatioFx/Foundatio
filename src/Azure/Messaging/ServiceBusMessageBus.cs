using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Serializer;
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

        private async void OnMessage(BrokeredMessage brokeredMessage) {
            Logger.Trace().Message($"OnMessage: {brokeredMessage.MessageId}").Write();
            var message = brokeredMessage.GetBody<MessageBusData>();

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
            var brokeredMessage = new BrokeredMessage(new MessageBusData { Type = messageType.AssemblyQualifiedName, Data = _serializer.SerializeToString(message) });
            
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                brokeredMessage.ScheduledEnqueueTimeUtc = DateTime.UtcNow.Add(delay.Value);
            }

            return _topicClient.SendAsync(brokeredMessage);
        }
    }
}
