using System;
using System.Diagnostics;
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
        private readonly string _subscriptionName;

        [Obsolete("Use the options overload")]
        public AzureServiceBusMessageBus(string connectionString, string topicName, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : this(new AzureServiceBusMessageBusOptions { ConnectionString = connectionString, Topic = topicName, SubscriptionName = "MessageBus", Serializer = serializer, LoggerFactory = loggerFactory }) { }

        public AzureServiceBusMessageBus(AzureServiceBusMessageBusOptions options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            _namespaceManager = NamespaceManager.CreateFromConnectionString(options.ConnectionString);
            _subscriptionName = _options.SubscriptionName ?? MessageBusId;
        }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_subscriptionClient != null)
                return;

            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

            using (await _lock.LockAsync().AnyContext()) {
                if (_subscriptionClient != null)
                    return;

                var sw = Stopwatch.StartNew();
                try {
                    await _namespaceManager.CreateSubscriptionAsync(CreateSubscriptionDescription()).AnyContext();
                } catch (MessagingEntityAlreadyExistsException) { }

                _subscriptionClient = SubscriptionClient.CreateFromConnectionString(_options.ConnectionString, _options.Topic, _subscriptionName, ReceiveMode.ReceiveAndDelete);
                _subscriptionClient.OnMessageAsync(OnMessageAsync, new OnMessageOptions { AutoComplete = true });
                if (_options.SubscriptionRetryPolicy != null)
                    _subscriptionClient.RetryPolicy = _options.SubscriptionRetryPolicy;
                if (_options.PrefetchCount.HasValue)
                    _subscriptionClient.PrefetchCount = _options.PrefetchCount.Value;

                sw.Stop();
                _logger.Trace("Ensure topic subscription exists took {0}ms.", sw.ElapsedMilliseconds);
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

                var sw = Stopwatch.StartNew();
                try {
                    await _namespaceManager.CreateTopicAsync(CreateTopicDescription()).AnyContext();
                } catch (MessagingEntityAlreadyExistsException) { }

                _topicClient = TopicClient.CreateFromConnectionString(_options.ConnectionString, _options.Topic);
                sw.Stop();
                _logger.Trace("Ensure topic exists took {0}ms.", sw.ElapsedMilliseconds);
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

        private TopicDescription CreateTopicDescription() {
            var td = new TopicDescription(_options.Topic);

            if (_options.TopicAutoDeleteOnIdle.HasValue)
                td.AutoDeleteOnIdle = _options.TopicAutoDeleteOnIdle.Value;

            if (_options.TopicDefaultMessageTimeToLive.HasValue)
                td.DefaultMessageTimeToLive = _options.TopicDefaultMessageTimeToLive.Value;

            if (_options.TopicMaxSizeInMegabytes.HasValue)
                td.MaxSizeInMegabytes = _options.TopicMaxSizeInMegabytes.Value;

            if (_options.TopicRequiresDuplicateDetection.HasValue)
                td.RequiresDuplicateDetection = _options.TopicRequiresDuplicateDetection.Value;

            if (_options.TopicDuplicateDetectionHistoryTimeWindow.HasValue)
                td.DuplicateDetectionHistoryTimeWindow = _options.TopicDuplicateDetectionHistoryTimeWindow.Value;

            if (_options.TopicEnableBatchedOperations.HasValue)
                td.EnableBatchedOperations = _options.TopicEnableBatchedOperations.Value;

            if (_options.TopicEnableFilteringMessagesBeforePublishing.HasValue)
                td.EnableFilteringMessagesBeforePublishing = _options.TopicEnableFilteringMessagesBeforePublishing.Value;

            if (_options.TopicIsAnonymousAccessible.HasValue)
                td.IsAnonymousAccessible = _options.TopicIsAnonymousAccessible.Value;

            if (_options.TopicStatus.HasValue)
                td.Status = _options.TopicStatus.Value;

            if (_options.TopicSupportOrdering.HasValue)
                td.SupportOrdering = _options.TopicSupportOrdering.Value;

            if (_options.TopicEnablePartitioning.HasValue)
                td.EnablePartitioning = _options.TopicEnablePartitioning.Value;

            if (_options.TopicEnableExpress.HasValue)
                td.EnableExpress = _options.TopicEnableExpress.Value;

            if (!String.IsNullOrEmpty(_options.TopicUserMetadata))
                td.UserMetadata = _options.TopicUserMetadata;

            return td;
        }

        private SubscriptionDescription CreateSubscriptionDescription() {
            var sd = new SubscriptionDescription(_options.Topic, _subscriptionName);

            if (_options.SubscriptionAutoDeleteOnIdle.HasValue)
                sd.AutoDeleteOnIdle = _options.SubscriptionAutoDeleteOnIdle.Value;

            if (_options.SubscriptionDefaultMessageTimeToLive.HasValue)
                sd.DefaultMessageTimeToLive = _options.SubscriptionDefaultMessageTimeToLive.Value;

            if (_options.SubscriptionWorkItemTimeout.HasValue)
                sd.LockDuration = _options.SubscriptionWorkItemTimeout.Value;

            if (_options.SubscriptionRequiresSession.HasValue)
                sd.RequiresSession = _options.SubscriptionRequiresSession.Value;

            if (_options.SubscriptionEnableDeadLetteringOnMessageExpiration.HasValue)
                sd.EnableDeadLetteringOnMessageExpiration = _options.SubscriptionEnableDeadLetteringOnMessageExpiration.Value;

            if (_options.SubscriptionEnableDeadLetteringOnFilterEvaluationExceptions.HasValue)
                sd.EnableDeadLetteringOnFilterEvaluationExceptions = _options.SubscriptionEnableDeadLetteringOnFilterEvaluationExceptions.Value;

            if (_options.SubscriptionMaxDeliveryCount.HasValue)
                sd.MaxDeliveryCount = _options.SubscriptionMaxDeliveryCount.Value;

            if (_options.SubscriptionEnableBatchedOperations.HasValue)
                sd.EnableBatchedOperations = _options.SubscriptionEnableBatchedOperations.Value;

            if (_options.SubscriptionStatus.HasValue)
                sd.Status = _options.SubscriptionStatus.Value;

            if (!String.IsNullOrEmpty(_options.SubscriptionForwardTo))
                sd.ForwardTo = _options.SubscriptionForwardTo;

            if (!String.IsNullOrEmpty(_options.SubscriptionForwardDeadLetteredMessagesTo))
                sd.ForwardDeadLetteredMessagesTo = _options.SubscriptionForwardDeadLetteredMessagesTo;

            if (!String.IsNullOrEmpty(_options.SubscriptionUserMetadata))
                sd.UserMetadata = _options.SubscriptionUserMetadata;

            return sd;
        }

        public override void Dispose() {
            base.Dispose();
            CloseTopicClient();
            CloseSubscriptionClient();
        }

        private void CloseTopicClient() {
            if (_topicClient == null)
                return;

            using (_lock.Lock()) {
                if (_topicClient == null)
                    return;

                _topicClient?.Close();
                _topicClient = null;
            }
        }

        private void CloseSubscriptionClient() {
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