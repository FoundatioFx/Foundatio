using System;
using System.Threading.Tasks;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Messaging {
    public class ServiceBusMessageBusTests : MessageBusTestBase {
        private static IMessageBus _messageBus;

        public ServiceBusMessageBusTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override IMessageBus GetMessageBus() {
            if (_messageBus != null)
                return _messageBus;

            if (ConnectionStrings.Get("ServiceBusConnectionString") == null)
                return null;

            _messageBus = new ServiceBusMessageBus(ConnectionStrings.Get("ServiceBusConnectionString"), Guid.NewGuid().ToString("N"));
            
            return _messageBus;
        }

        [Fact]
        public override Task CanSendMessage() {
            return base.CanSendMessage();
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
        public override Task WontKeepMessagesWithNoSubscribers() {
            return base.WontKeepMessagesWithNoSubscribers();
        }
    }
}