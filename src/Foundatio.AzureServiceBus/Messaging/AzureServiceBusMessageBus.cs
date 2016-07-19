using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Messaging {
    public class AzureServiceBusMessageBus : MessageBusBase, IMessageBus {
        private readonly ISerializer _serializer;
        private readonly TopicClient _topicClient;
        private readonly SubscriptionClient _subscriptionClient;
        
        public AzureServiceBusMessageBus(string connectionString, string topicName, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _serializer = serializer ?? new JsonNetSerializer();

            var namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            if (!namespaceManager.TopicExists(topicName))
                namespaceManager.CreateTopic(topicName);

            _topicClient = TopicClient.CreateFromConnectionString(connectionString, topicName);

            const string subscriptionName = "MessageBus";
            if (!namespaceManager.SubscriptionExists(topicName, subscriptionName))
                namespaceManager.CreateSubscription(topicName, subscriptionName);

            _subscriptionClient = SubscriptionClient.CreateFromConnectionString(connectionString, topicName, subscriptionName, ReceiveMode.ReceiveAndDelete);
            _subscriptionClient.OnMessageAsync(OnMessageAsync, new OnMessageOptions { AutoComplete = true });
        }

        private async Task OnMessageAsync(BrokeredMessage brokeredMessage) {
            _logger.Trace("OnMessage: {messageId}", brokeredMessage.MessageId);
            var message = brokeredMessage.GetBody<MessageBusData>();

            Type messageType;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                _logger.Error(ex, "Error getting message body type: {0}", ex.Message);
                return;
            }

            object body = await _serializer.DeserializeAsync(message.Data, messageType).AnyContext();
            await SendMessageToSubscribersAsync(messageType, body).AnyContext();
        }

        public override async Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null)
                return;

            var brokeredMessage = new BrokeredMessage(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext() 
            });

            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                brokeredMessage.ScheduledEnqueueTimeUtc = SystemClock.UtcNow.Add(delay.Value);

            await _topicClient.SendAsync(brokeredMessage).AnyContext();
        }

        public override void Dispose() {
            base.Dispose();
            _subscriptionClient.Close();
            _topicClient.Close();
        }
    }
}
