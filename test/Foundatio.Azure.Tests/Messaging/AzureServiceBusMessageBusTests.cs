using System;
using System.Threading.Tasks;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Messaging {
    public class AzureServiceBusMessageBusTests : MessageBusTestBase {
        protected readonly string _topicName = Guid.NewGuid().ToString("N");

        public AzureServiceBusMessageBusTests(ITestOutputHelper output) : base(output) {}

        protected override IMessageBus GetMessageBus() {
            if (String.IsNullOrEmpty(Configuration.GetConnectionString("ServiceBusConnectionString")))
                return null;

            return new AzureServiceBusMessageBus(Configuration.GetConnectionString("ServiceBusConnectionString"), _topicName, loggerFactory: Log);
        }

        [Fact]
        public override Task CanSendMessage() {
            return base.CanSendMessage();
        }
        
        [Fact]
        public override Task CanHandleNullMessage() {
            return base.CanHandleNullMessage();
        }

        [Fact]
        public override Task CanSendDerivedMessage() {
            return base.CanSendDerivedMessage();
        }

        [Fact]
        public override Task CanSendDelayedMessage() {
            return base.CanSendDelayedMessage();
        }

        [Fact]
        public override Task CanSendMessageToMultipleSubscribers() {
            return base.CanSendMessageToMultipleSubscribers();
        }

        [Fact]
        public override Task CanTolerateSubscriberFailure() {
            return base.CanTolerateSubscriberFailure();
        }

        [Fact]
        public override Task WillOnlyReceiveSubscribedMessageType() {
            return base.WillOnlyReceiveSubscribedMessageType();
        }

        [Fact]
        public override Task WillReceiveDerivedMessageTypes() {
            return base.WillReceiveDerivedMessageTypes();
        }

        [Fact]
        public override Task CanSubscribeToAllMessageTypes() {
            return base.CanSubscribeToAllMessageTypes();
        }
        
        [Fact]
        public override Task CanCancelSubscription() {
            return base.CanCancelSubscription();
        }

        [Fact]
        public override Task WontKeepMessagesWithNoSubscribers() {
            return base.WontKeepMessagesWithNoSubscribers();
        }

        [Fact]
        public override Task CanReceiveFromMultipleSubscribers() {
            return base.CanReceiveFromMultipleSubscribers();
        }

        [Fact]
        public override void CanDisposeWithNoSubscribersOrPublishers() {
            base.CanDisposeWithNoSubscribersOrPublishers();
        }
    }
}