#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Xunit;
using Foundatio.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public abstract class MessageBusTestBase : CaptureTests {
        protected MessageBusTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected virtual IMessageBus GetMessageBus() {
            return null;
        }

        public virtual async Task CanSendMessage() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AutoResetEvent(false);
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Logger.Trace().Message("Got message").Write();
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    Logger.Trace().Message("Set event").Write();
                });

                await Task.Delay(100).AnyContext();
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();
                Trace.WriteLine("Published one...");

                bool success = resetEvent.WaitOne(5000);
                Trace.WriteLine("Done waiting: " + success);
                Assert.True(success, "Failed to receive message.");
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task CanSendDelayedMessage() {
            const int numConcurrentMessages = 10000;
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new CountDownLatch(numConcurrentMessages);

                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Logger.Trace().Message("Got message").Write();
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Signal();
                    Logger.Trace().Message("Set event").Write();
                });

                var sw = new Stopwatch();
                sw.Start();

                Parallel.For(0, numConcurrentMessages, (_) => {
                    messageBus.PublishAsync(new SimpleMessageA {
                        Data = "Hello"
                    }, TimeSpan.FromMilliseconds(RandomData.GetInt(0, 300))).AnyContext().GetAwaiter().GetResult();
                    Logger.Trace().Message("Published one...").Write();
                });

                bool success = resetEvent.Wait(2000);
                sw.Stop();
                Logger.Trace().Message("Done waiting: " + success).Write();

                Assert.True(success, "Failed to receive message.");
                Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(80));
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task CanSendMessageToMultipleSubscribers() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(3);
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                bool success = latch.Wait(2000);
                Assert.True(success, "Failed to receive all messages.");
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task CanTolerateSubscriberFailure() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(2);
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    throw new ApplicationException();
                });
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                bool success = latch.Wait(2000);
                Assert.True(success, "Failed to receive all messages.");
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task WillOnlyReceiveSubscribedMessageType() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AutoResetEvent(false);
                messageBus.SubscribeAsync<SimpleMessageB>(msg => {
                    Assert.True(false, "Received wrong message type.");
                });
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });
                messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                bool success = resetEvent.WaitOne(2000);
                Assert.True(success, "Failed to receive message.");
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task WillReceiveDerivedMessageTypes() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(2);
                messageBus.SubscribeAsync<ISimpleMessage>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    latch.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();
                await messageBus.PublishAsync(new SimpleMessageB {
                    Data = "Hello"
                }).AnyContext();
                await messageBus.PublishAsync(new SimpleMessageC {
                    Data = "Hello"
                }).AnyContext();

                bool success = latch.Wait(5000);
                Assert.True(success, "Failed to receive all messages.");
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task CanSubscribeToAllMessageTypes() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var latch = new CountDownLatch(3);
                messageBus.SubscribeAsync<object>(msg => {
                    latch.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();
                await messageBus.PublishAsync(new SimpleMessageB {
                    Data = "Hello"
                }).AnyContext();
                await messageBus.PublishAsync(new SimpleMessageC {
                    Data = "Hello"
                }).AnyContext();

                bool success = latch.Wait(2000);
                Assert.True(success, "Failed to receive all messages.");
            }

            await Task.Delay(50).AnyContext();
        }

        public virtual async Task WontKeepMessagesWithNoSubscribers() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();

                await Task.Delay(100).AnyContext();
                var resetEvent = new AutoResetEvent(false);
                messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });

                bool success = resetEvent.WaitOne(100);
                Assert.False(success, "Messages are building up.");
            }
        }
    }
}

#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed