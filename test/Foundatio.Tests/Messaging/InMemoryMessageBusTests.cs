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
            return base.CanSendDelayedMessageAsync();
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

        public void Dispose() {
            _messageBus?.Dispose();
            _messageBus = null;
        }
    }
}