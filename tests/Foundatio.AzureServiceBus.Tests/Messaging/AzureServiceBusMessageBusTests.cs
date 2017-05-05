using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Messaging {
    public class AzureServiceBusMessageBusTests : MessageBusTestBase {
        public AzureServiceBusMessageBusTests(ITestOutputHelper output) : base(output) {}

        protected override IMessageBus GetMessageBus() {
            string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;

            return new AzureServiceBusMessageBus(new AzureServiceBusMessageBusOptions {
                ConnectionString = connectionString,
                Topic = "test-messages",
                TopicEnableBatchedOperations = true,
                TopicEnableExpress = true,
                TopicEnablePartitioning = true,
                TopicSupportOrdering = false,
                TopicRequiresDuplicateDetection = false,
                SubscriptionAutoDeleteOnIdle = TimeSpan.FromMinutes(5),
                SubscriptionEnableBatchedOperations = true,
                SubscriptionMaxDeliveryCount = int.MaxValue,
                PrefetchCount = 500,
                LoggerFactory = Log
            });
        }

        [Fact]
        public override Task CanSendMessageAsync() {
            return base.CanSendMessageAsync();
        }

        [Fact]
        public override Task CanHandleNullMessageAsync() {
            return base.CanHandleNullMessageAsync();
        }

        [Fact]
        public override Task CanSendDerivedMessageAsync() {
            return base.CanSendDerivedMessageAsync();
        }

        [Fact]
        public override Task CanSendDelayedMessageAsync() {
            Log.SetLogLevel<AzureServiceBusMessageBus>(LogLevel.Information);
            return base.CanSendDelayedMessageAsync();
        }

        [Fact]
        public override Task CanSubscribeConcurrentlyAsync() {
            return base.CanSubscribeConcurrentlyAsync();
        }

        [Fact]
        public override Task CanReceiveMessagesConcurrentlyAsync() {
            return base.CanReceiveMessagesConcurrentlyAsync();
        }

        [Fact]
        public override Task CanSendMessageToMultipleSubscribersAsync() {
            return base.CanSendMessageToMultipleSubscribersAsync();
        }

        [Fact]
        public override Task CanTolerateSubscriberFailureAsync() {
            return base.CanTolerateSubscriberFailureAsync();
        }

        [Fact]
        public override Task WillOnlyReceiveSubscribedMessageTypeAsync() {
            return base.WillOnlyReceiveSubscribedMessageTypeAsync();
        }

        [Fact]
        public override Task WillReceiveDerivedMessageTypesAsync() {
            return base.WillReceiveDerivedMessageTypesAsync();
        }

        [Fact]
        public override Task CanSubscribeToAllMessageTypesAsync() {
            return base.CanSubscribeToAllMessageTypesAsync();
        }

        [Fact]
        public override Task CanCancelSubscriptionAsync() {
            return base.CanCancelSubscriptionAsync();
        }

        [Fact]
        public override Task WontKeepMessagesWithNoSubscribersAsync() {
            return base.WontKeepMessagesWithNoSubscribersAsync();
        }

        [Fact]
        public override Task CanReceiveFromMultipleSubscribersAsync() {
            return base.CanReceiveFromMultipleSubscribersAsync();
        }

        [Fact]
        public override void CanDisposeWithNoSubscribersOrPublishers() {
            base.CanDisposeWithNoSubscribersOrPublishers();
        }
    }
}