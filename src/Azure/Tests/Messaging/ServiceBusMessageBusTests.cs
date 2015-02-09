using System;
using System.Threading;
using Foundatio.Azure.Messaging;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.Azure.Tests.Messaging {
    public class ServiceBusMessageBusTests {
        private readonly ServiceBusMessageBus _messageBus;

        public ServiceBusMessageBusTests() {
            if (ConnectionStrings.Get("ServiceBusConnectionString") == null)
                return;

            _messageBus = new ServiceBusMessageBus(ConnectionStrings.Get("ServiceBusConnectionString"), Guid.NewGuid().ToString("N"));   
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
    }
}