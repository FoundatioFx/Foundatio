using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging {
    public class InMemoryMessageBusTests : MessageBusTestBase {
        protected override IMessageBus GetMessageBus() {
            return new InMemoryMessageBus();
        }

        [Fact]
        public override void CanSendMessage() {
            base.CanSendMessage();
        }

        [Fact]
        public override void CanSendDelayedMessage() {
            base.CanSendDelayedMessage();
        }

        [Fact]
        public override void CanSendMessageToMultipleSubscribers() {
            base.CanSendMessageToMultipleSubscribers();
        }

        [Fact]
        public override void CanTolerateSubscriberFailure() {
            base.CanTolerateSubscriberFailure();
        }

        [Fact]
        public override void WillOnlyReceiveSubscribedMessageType() {
            base.WillOnlyReceiveSubscribedMessageType();
        }

        [Fact]
        public override void AWillReceiveDerivedMessageTypes() {
            base.AWillReceiveDerivedMessageTypes();
        }

        [Fact]
        public override void CanSubscribeToAllMessageTypes() {
            base.CanSubscribeToAllMessageTypes();
        }

        [Fact]
        public override void WontKeepMessagesWithNoSubscribers() {
            base.WontKeepMessagesWithNoSubscribers();
        }
    }
}