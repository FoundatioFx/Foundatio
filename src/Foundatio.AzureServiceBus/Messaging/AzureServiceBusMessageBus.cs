using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace Foundatio.Messaging {
    public class AzureServiceBusMessageBus : MessageBusBase<AzureServiceBusMessageBusOptions> {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly ManagementClient _managementClient;
        private TopicClient _topicClient;
        private SubscriptionClient _subscriptionClient;
        private readonly string _subscriptionName;

        public AzureServiceBusMessageBus(AzureServiceBusMessageBusOptions options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            _managementClient = new ManagementClient(options.ConnectionString);
            _subscriptionName = _options.SubscriptionName ?? MessageBusId;
        }

        public AzureServiceBusMessageBus(Builder<AzureServiceBusMessageBusOptionsBuilder, AzureServiceBusMessageBusOptions> config)
            : this(config(new AzureServiceBusMessageBusOptionsBuilder()).Build()) { }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_subscriptionClient != null)
                return;

            if (!TopicIsCreated)
                await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

            using (await _lock.LockAsync().AnyContext()) {
                if (_subscriptionClient != null)
                    return;

                var sw = Stopwatch.StartNew();
                try {
                    await _managementClient.CreateSubscriptionAsync(CreateSubscriptionDescription(), cancellationToken).AnyContext();
                } catch (MessagingEntityAlreadyExistsException) { }

                // Look into message factory with multiple receivers so more than one connection is made and managed....
                _subscriptionClient = new SubscriptionClient(_options.ConnectionString, _options.Topic, _subscriptionName, _options.SubscriptionReceiveMode, _options.SubscriptionRetryPolicy);
                _subscriptionClient.RegisterMessageHandler(OnMessageAsync, new MessageHandlerOptions(MessageHandlerException) {
                    /* AutoComplete = true, // Don't run with receive and delete */ MaxConcurrentCalls = 6 /* calculate this based on the the thread count. */ });
                if (_options.PrefetchCount.HasValue)
                    _subscriptionClient.PrefetchCount = _options.PrefetchCount.Value;
                sw.Stop();
                _logger.LogTrace("Ensure topic subscription exists took {0}ms.", sw.ElapsedMilliseconds);
            }
        }

        private Task OnMessageAsync(Microsoft.Azure.ServiceBus.Message brokeredMessage, CancellationToken cancellationToken) {
            if (_subscribers.IsEmpty)
                return Task.CompletedTask;

            _logger.LogTrace("OnMessageAsync({messageId})", brokeredMessage.MessageId);
            var message = new Message(() => DeserializeMessageBody(brokeredMessage.ContentType, brokeredMessage.Body)) {
                Data = brokeredMessage.Body,
                Type = brokeredMessage.ContentType
            };

            return SendMessageToSubscribersAsync(message);
        }

        private Task MessageHandlerException(ExceptionReceivedEventArgs e) {
            _logger.LogWarning("Exception: \"{0}\" {0}", e.Exception.Message, e.ExceptionReceivedContext.EntityPath);
            return Task.CompletedTask;
        }

        private bool TopicIsCreated => _topicClient != null;
        protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) {
            if (TopicIsCreated)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (TopicIsCreated)
                    return;

                var sw = Stopwatch.StartNew();
                try {
                    await _managementClient.CreateTopicAsync(CreateTopicDescription()).AnyContext();
                } catch (MessagingEntityAlreadyExistsException) { }

                _topicClient = new TopicClient(_options.ConnectionString, _options.Topic);
                sw.Stop();
                _logger.LogTrace("Ensure topic exists took {0}ms.", sw.ElapsedMilliseconds);
            }
        }

        protected override Task PublishImplAsync(string messageType, object message, TimeSpan? delay, QueueEntryOptions options, CancellationToken cancellationToken) {
            var brokeredMessage = new Microsoft.Azure.ServiceBus.Message(_serializer.SerializeToBytes(message));
            brokeredMessage.MessageId = options.UniqueId;
            brokeredMessage.ContentType = messageType;

            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                _logger.LogTrace("Schedule delayed message: {messageType} ({delay}ms)", messageType, delay.Value.TotalMilliseconds);
                brokeredMessage.ScheduledEnqueueTimeUtc = SystemClock.UtcNow.Add(delay.Value);
            } else {
                _logger.LogTrace("Message Publish: {messageType}", messageType);
            }

            return _topicClient.SendAsync(brokeredMessage);
        }

        private TopicDescription CreateTopicDescription() {
            var td = new TopicDescription(_options.Topic);

            if (_options.TopicAutoDeleteOnIdle.HasValue)
                td.AutoDeleteOnIdle = _options.TopicAutoDeleteOnIdle.Value;

            if (_options.TopicDefaultMessageTimeToLive.HasValue)
                td.DefaultMessageTimeToLive = _options.TopicDefaultMessageTimeToLive.Value;

            if (_options.TopicMaxSizeInMegabytes.HasValue)
                td.MaxSizeInMB = _options.TopicMaxSizeInMegabytes.Value;

            if (_options.TopicRequiresDuplicateDetection.HasValue)
                td.RequiresDuplicateDetection = _options.TopicRequiresDuplicateDetection.Value;

            if (_options.TopicDuplicateDetectionHistoryTimeWindow.HasValue)
                td.DuplicateDetectionHistoryTimeWindow = _options.TopicDuplicateDetectionHistoryTimeWindow.Value;

            if (_options.TopicEnableBatchedOperations.HasValue)
                td.EnableBatchedOperations = _options.TopicEnableBatchedOperations.Value;

            if (_options.TopicStatus.HasValue)
                td.Status = _options.TopicStatus.Value;

            if (_options.TopicSupportOrdering.HasValue)
                td.SupportOrdering = _options.TopicSupportOrdering.Value;

            if (_options.TopicEnablePartitioning.HasValue)
                td.EnablePartitioning = _options.TopicEnablePartitioning.Value;

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
            _managementClient?.CloseAsync();
        }

        private void CloseTopicClient() {
            if (_topicClient == null)
                return;

            using (_lock.Lock()) {
                if (_topicClient == null)
                    return;

                _topicClient?.CloseAsync();
                _topicClient = null;
            }
        }

        private void CloseSubscriptionClient() {
            if (_subscriptionClient == null)
                return;

            using (_lock.Lock()) {
                if (_subscriptionClient == null)
                    return;

                _subscriptionClient?.CloseAsync();
                _subscriptionClient = null;
            }
        }
    }
}