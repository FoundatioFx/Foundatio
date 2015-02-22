using System;
using Foundatio.Messaging;
using Foundatio.Redis.Messaging;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Utility;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Messaging {
    public class RedisMessageBusTests : MessageBusTestBase {
        protected override IMessageBus GetMessageBus() {
            if (ConnectionStrings.Get("RedisConnectionString") == null)
                return null;

            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
            return new RedisMessageBus(muxer.GetSubscriber(), "test-messages");
        }

        [Fact]
        public override void CanSendMessage() {
            base.CanSendMessage();
        }

        //[Fact]
        //public override void CanSendDelayedMessage() {
        //    base.CanSendDelayedMessage();
        //}

        //[Fact]
        //public override void CanSendMessageToMultipleSubscribers() {
        //    base.CanSendMessageToMultipleSubscribers();
        //}

        //[Fact]
        //public override void CanTolerateSubscriberFailure() {
        //    base.CanTolerateSubscriberFailure();
        //}

        //[Fact]
        //public override void WillOnlyReceiveSubscribedMessageType() {
        //    base.WillOnlyReceiveSubscribedMessageType();
        //}

        [Fact]
        public override void AWillReceiveDerivedMessageTypes() {
            base.AWillReceiveDerivedMessageTypes();
        }

        //[Fact]
        //public override void CanSubscribeToAllMessageTypes() {
        //    base.CanSubscribeToAllMessageTypes();
        //}

        //[Fact]
        //public override void WontKeepMessagesWithNoSubscribers() {
        //    base.WontKeepMessagesWithNoSubscribers();
        //}
    }
}