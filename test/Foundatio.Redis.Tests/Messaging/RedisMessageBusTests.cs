﻿using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Messaging {
    public class RedisMessageBusTests : MessageBusTestBase {
        public RedisMessageBusTests(ITestOutputHelper output) : base(output) {
            FlushAll();
        }

        protected override IMessageBus GetMessageBus() {
            return new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber(), "test-messages", loggerFactory: Log);
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

        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error flushing redis");
                }
            }
        }
    }
}