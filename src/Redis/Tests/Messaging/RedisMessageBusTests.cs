using System;
using System.Threading;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Messaging {
    public class RedisMessageBusTests : IDisposable {
        private readonly RedisMessageBus _messageBus;

        public RedisMessageBusTests() {
            if (ConnectionStrings.Get("RedisConnectionString") == null)
                return;

            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
            _messageBus = new RedisMessageBus(muxer.GetSubscriber(), Guid.NewGuid().ToString("N"));   
        }

        [Fact]
        public void CanSendMessage() {
            if (_messageBus == null)
                return;

            var resetEvent = new AutoResetEvent(false);
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                resetEvent.Set();
            });
            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });

            bool success = resetEvent.WaitOne(2000);
            Assert.True(success, "Failed to receive message.");
        }

        [Fact]
        public void WontKeepMessagesWithNoSubscribers() {
            if (_messageBus == null)
                return;

            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });

            Thread.Sleep(1000);
            var resetEvent = new AutoResetEvent(false);
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                resetEvent.Set();
            });

            bool success = resetEvent.WaitOne(1000);
            Assert.False(success, "Messages are building up.");
        }

        [Fact]
        public void CanSendMessageToMultipleSubscribers() {
            if (_messageBus == null)
                return;

            var latch = new CountDownLatch(3);
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                latch.Signal();
            });
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                latch.Signal();
            });
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                latch.Signal();
            });
            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });

            bool success = latch.Wait(3000);
            Assert.True(success, "Failed to receive all messages.");
        }

        [Fact]
        public void CanTolerateSubscriberFailure() {
            if (_messageBus == null)
                return;

            var latch = new CountDownLatch(2);
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                throw new ApplicationException();
            });
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                latch.Signal();
            });
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                latch.Signal();
            });
            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });

            bool success = latch.Wait(3000);
            Assert.True(success, "Failed to receive all messages.");
        }

        [Fact]
        public void WillOnlyReceiveSubscribedMessageType() {
            if (_messageBus == null)
                return;

            var resetEvent = new AutoResetEvent(false);
            _messageBus.Subscribe<SimpleMessageB>(msg => {
                Assert.True(false, "Received wrong message type.");
            });
            _messageBus.Subscribe<SimpleMessageA>(msg => {
                Assert.Equal("Hello", msg.Data);
                resetEvent.Set();
            });
            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });

            bool success = resetEvent.WaitOne(3000);
            Assert.True(success, "Failed to receive message.");
        }

        [Fact]
        public void WillReceiveDerivedMessageTypes() {
            if (_messageBus == null)
                return;

            var latch = new CountDownLatch(2);
            _messageBus.Subscribe<ISimpleMessage>(msg => {
                Assert.Equal("Hello", msg.Data);
                latch.Signal();
            });
            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });
            _messageBus.Publish(new SimpleMessageB {
                Data = "Hello"
            });
            _messageBus.Publish(new SimpleMessageC {
                Data = "Hello"
            });

            bool success = latch.Wait(3000);
            Assert.True(success, "Failed to receive all messages.");
        }

        [Fact]
        public void CanSubscribeToAllMessageTypes() {
            if (_messageBus == null)
                return;

            var latch = new CountDownLatch(3);
            _messageBus.Subscribe<object>(msg => {
                latch.Signal();
            });
            _messageBus.Publish(new SimpleMessageA {
                Data = "Hello"
            });
            _messageBus.Publish(new SimpleMessageB {
                Data = "Hello"
            });
            _messageBus.Publish(new SimpleMessageC {
                Data = "Hello"
            });

            bool success = latch.Wait(3000);
            Assert.True(success, "Failed to receive all messages.");
        }

        public void Dispose() {
            _messageBus.Dispose();
        }
    }
}