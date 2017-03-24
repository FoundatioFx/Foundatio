using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Nito.AsyncEx;

namespace Foundatio.Messaging {
    public class AzureServiceBusMessageBus : MessageBusBase<AzureServiceBusMessageBusOptions> {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly NamespaceManager _namespaceManager;
        private TopicClient _topicClient;
        private SubscriptionClient _subscriptionClient;

        [Obsolete("Use the options overload")]
        public AzureServiceBusMessageBus(string connectionString, string topicName, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : this(new AzureServiceBusMessageBusOptions { ConnectionString = connectionString, Topic = topicName, Serializer = serializer, LoggerFactory = loggerFactory }) { }

        public AzureServiceBusMessageBus(AzureServiceBusMessageBusOptions options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            _namespaceManager = NamespaceManager.CreateFromConnectionString(options.ConnectionString);
        }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_subscriptionClient != null)
                return;

            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

            using (await _lock.LockAsync().AnyContext()) {
                if (_subscriptionClient != null)
                    return;

                await _namespaceManager.CreateSubscriptionAsync(new SubscriptionDescription(_options.Topic, MessageBusId) {
                    AutoDeleteOnIdle = TimeSpan.FromHours(1),
                    EnableBatchedOperations = true
                }).AnyContext();

                _subscriptionClient = SubscriptionClient.CreateFromConnectionString(_options.ConnectionString, _options.Topic, MessageBusId, ReceiveMode.ReceiveAndDelete);
                _subscriptionClient.OnMessageAsync(OnMessageAsync, new OnMessageOptions { AutoComplete = true });
            }
        }

        private Task OnMessageAsync(BrokeredMessage brokeredMessage) {
            if (_subscribers.IsEmpty)
                return Task.CompletedTask;

            _logger.Trace("OnMessageAsync({messageId})", brokeredMessage.MessageId);
            MessageBusData message;
            try {
                message = brokeredMessage.GetBody<MessageBusData>();
            }
            catch (Exception ex) {
                _logger.Warn(ex, "OnMessageAsync({0}) Error while deserializing messsage: {1}", brokeredMessage.MessageId, ex.Message);
                return brokeredMessage.DeadLetterAsync("Deserialization error", ex.Message);
            }

            return SendMessageToSubscribersAsync(message, _serializer);
        }

        protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) {
            if (_topicClient != null)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_topicClient != null)
                    return;

                if (!await _namespaceManager.TopicExistsAsync(_options.Topic).AnyContext())
                    await _namespaceManager.CreateTopicAsync(new TopicDescription(_options.Topic) { }).AnyContext();

                _topicClient = TopicClient.CreateFromConnectionString(_options.ConnectionString, _options.Topic);
            }
        }

        protected override async Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            var brokeredMessage = new BrokeredMessage(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext()
            });

            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                _logger.Trace("Schedule delayed message: {messageType} ({delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                brokeredMessage.ScheduledEnqueueTimeUtc = SystemClock.UtcNow.Add(delay.Value);
            } else {
                _logger.Trace("Message Publish: {messageType}", messageType.FullName);
            }

            await _topicClient.SendAsync(brokeredMessage).AnyContext();
        }

        public override void Dispose() {
            try {
                base.Dispose();
                ClosePublisherConnection();
                CloseSubscriberConnection();
            } finally {
                _namespaceManager.DeleteSubscription(_options.Topic, MessageBusId);
            }
        }

        private void ClosePublisherConnection() {
            if (_topicClient == null)
                return;

            using (_lock.Lock()) {
                if (_topicClient == null)
                    return;

                _topicClient?.Close();
                _topicClient = null;
            }
        }

        private void CloseSubscriberConnection() {
            if (_subscriptionClient == null)
                return;

            using (_lock.Lock()) {
                if (_subscriptionClient == null)
                    return;

                _subscriptionClient?.Close();
                _subscriptionClient = null;
            }
        }
    }
}