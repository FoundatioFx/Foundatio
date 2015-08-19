using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public class InMemoryMessageBusTests : MessageBusTestBase {
        public InMemoryMessageBusTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            MinimumLogLevel = LogLevel.Warn;
        }

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
        public override void WillReceiveDerivedMessageTypes() {
            base.WillReceiveDerivedMessageTypes();
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