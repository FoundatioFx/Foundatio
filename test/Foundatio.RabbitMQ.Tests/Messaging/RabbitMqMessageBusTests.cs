using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Tests;
using Foundatio.Messaging;
using Foundatio.RabbitMQ;
using Foundatio.RabbitMQ.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.RabbitMQ.Tests.Messaging {
    public class RabbitMqMessageBusTests : MessageBusTestBase {
        private IMessageBus _messageBus;
        public RabbitMqMessageBusTests(ITestOutputHelper output) : base(output) { }

        protected override IMessageBus GetMessageBus() {
            if (_messageBus != null)
                return _messageBus;

            _messageBus = new RabbitMQMessageService("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioExchange", true, true, TimeSpan.FromMilliseconds(50), loggerFactory: Log);
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

    }
}
