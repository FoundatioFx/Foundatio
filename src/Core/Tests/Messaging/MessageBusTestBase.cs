using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Xunit;
using Foundatio.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public abstract class MessageBusTestBase : CaptureTests {
        protected MessageBusTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected virtual IMessageBus GetMessageBus() {
            return null;
        }

        public virtual async Task CanSendMessage() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AutoResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Logger.Trace().Message("Got message").Write();
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    Logger.Trace().Message("Set event").Write();
                });
                Thread.Sleep(100);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                Trace.WriteLine("Published one...");

                bool success = resetEvent.WaitOne(5000);
                Trace.WriteLine("Done waiting: " + success);
                Assert.True(success, "Failed to receive message.");
            }

            Thread.Sleep(50);
        }

        public virtual async Task CanSendDelayedMessage() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AutoResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Logger.Trace().Message("Got message").Write();
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    Logger.Trace().Message("Set event").Write();
                });

                var sw = new Stopwatch();
                sw.Start();
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }, TimeSpan.FromMilliseconds(100));
                Logger.Trace().Message("Published one...").Write();

                bool success = resetEvent.WaitOne(2000);
                sw.Stop();
                Logger.Trace().Message("Done waiting: " + success).Write();

                Assert.True(success, "Failed to receive message.");
                Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(80));
            }

            Thread.Sleep(50);
        }

        public virtual async Task CanSendMessageToMultipleSubscribers() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(3);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                bool success = latch.Wait(2000);
                Assert.True(success, "Failed to receive all messages.");
            }

            Thread.Sleep(50);
        }

        public virtual async Task CanTolerateSubscriberFailure() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(2);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    throw new ApplicationException();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                bool success = latch.Wait(2000);
                Assert.True(success, "Failed to receive all messages.");
            }

            Thread.Sleep(50);
        }

        public virtual async Task WillOnlyReceiveSubscribedMessageType() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AutoResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageB>(msg => {
                    Assert.True(false, "Received wrong message type.");
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                bool success = resetEvent.WaitOne(2000);
                Assert.True(success, "Failed to receive message.");
            }

            Thread.Sleep(50);
        }

        public virtual async Task WillReceiveDerivedMessageTypes() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(2);
                await messageBus.SubscribeAsync<ISimpleMessage>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                await messageBus.PublishAsync(new SimpleMessageB {
                    Data = "Hello"
                });
                await messageBus.PublishAsync(new SimpleMessageC {
                    Data = "Hello"
                });

                bool success = latch.Wait(5000);
                Assert.True(success, "Failed to receive all messages.");
            }

            Thread.Sleep(50);
        }

        public virtual async Task CanSubscribeToAllMessageTypes() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(3);
                await messageBus.SubscribeAsync<object>(msg => {
                    latch.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                await messageBus.PublishAsync(new SimpleMessageB {
                    Data = "Hello"
                });
                await messageBus.PublishAsync(new SimpleMessageC {
                    Data = "Hello"
                });

                bool success = latch.Wait(2000);
                Assert.True(success, "Failed to receive all messages.");
            }

            Thread.Sleep(50);
        }

        public virtual async Task WontKeepMessagesWithNoSubscribers() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                Thread.Sleep(1000);
                var resetEvent = new AutoResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });

                bool success = resetEvent.WaitOne(2000);
                Assert.False(success, "Messages are building up.");
            }

            Thread.Sleep(50);
        }
    }
}
