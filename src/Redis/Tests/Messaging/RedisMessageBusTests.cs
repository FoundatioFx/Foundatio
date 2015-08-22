using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Messaging {
    public class RedisMessageBusTests : MessageBusTestBase {
        public RedisMessageBusTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override IMessageBus GetMessageBus() {
            return new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber(), "test-messages");
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