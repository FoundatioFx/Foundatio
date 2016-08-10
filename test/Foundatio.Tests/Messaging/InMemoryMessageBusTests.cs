using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public class InMemoryMessageBusTests : MessageBusTestBase, IDisposable {
        private IMessageBus _messageBus;

        public InMemoryMessageBusTests(ITestOutputHelper output) : base(output) {}

        protected override IMessageBus GetMessageBus() {
            if (_messageBus != null)
                return _messageBus;

            _messageBus = new InMemoryMessageBus(Log);
            return _messageBus;
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

        public void Dispose() {
            _messageBus?.Dispose();
            _messageBus = null;
        }
    }
}