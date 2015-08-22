using System;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Messaging {
    public class ServiceBusMessageBusTests : MessageBusTestBase {
        private static IMessageBus _messageBus;

        public ServiceBusMessageBusTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override IMessageBus GetMessageBus() {
            if (_messageBus != null)
                return _messageBus;

            if (ConnectionStrings.Get("ServiceBusConnectionString") == null)
                return null;

            _messageBus = new ServiceBusMessageBus(ConnectionStrings.Get("ServiceBusConnectionString"), Guid.NewGuid().ToString("N"));
            
            return _messageBus;
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