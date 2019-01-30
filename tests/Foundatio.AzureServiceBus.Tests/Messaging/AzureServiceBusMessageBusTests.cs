using System;
using System.Threading.Tasks;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AzureServiceBus.Tests.Messaging {
    public class AzureServiceBusMessageBusTests : MessageBusTestBase {
        public AzureServiceBusMessageBusTests(ITestOutputHelper output) : base(output) {}

        protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null) {
            string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;
            
            return new AzureServiceBusMessageBus(o => {
                o.ConnectionString(connectionString);
                o.Topic("test-messages");
                o.TopicEnableBatchedOperations(true);
                o.TopicEnableExpress(true);
                o.TopicEnablePartitioning(true);
                o.TopicSupportOrdering(false);
                o.TopicRequiresDuplicateDetection(false);
                o.SubscriptionAutoDeleteOnIdle(TimeSpan.FromMinutes(5));
                o.SubscriptionEnableBatchedOperations(true);
                o.SubscriptionMaxDeliveryCount(Int32.MaxValue);
                o.PrefetchCount(500);
                o.LoggerFactory(Log);
                
                if (config != null)
                config(o.Target);

                return o;
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