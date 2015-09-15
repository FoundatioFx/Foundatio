using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Xunit;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;
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
                var resetEvent = new AsyncManualResetEvent(false);
                messageBus.Subscribe<SimpleMessageA>(msg => {
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

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(5)).AnyContext();
            }
        }

        public virtual async Task CanSendDelayedMessage() {
            const int numConcurrentMessages = 10000;
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var countdown = new AsyncCountdownEvent(numConcurrentMessages);

                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Logger.Trace().Message("Got message").Write();
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                    Logger.Trace().Message("Set event").Write();
                });

                var sw = Stopwatch.StartNew();

                await Run.InParallel(numConcurrentMessages, async i => {
                    await messageBus.PublishAsync(new SimpleMessageA {
                        Data = "Hello"
                    }, TimeSpan.FromMilliseconds(RandomData.GetInt(0, 300))).AnyContext();
                    Logger.Trace().Message("Published one...").Write();
                }).AnyContext();

                await countdown.WaitAsync(TimeSpan.FromSeconds(2)).AnyContext();
                sw.Stop();
                
                Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(80));
            }
        }

        public virtual async Task CanSendMessageToMultipleSubscribers() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var countdown = new AsyncCountdownEvent(3);
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();

                await countdown.WaitAsync(TimeSpan.FromSeconds(2)).AnyContext();
            }
        }

        public virtual async Task CanTolerateSubscriberFailure() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var countdown = new AsyncCountdownEvent(2);
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    throw new ApplicationException();
                });
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();

                await countdown.WaitAsync(TimeSpan.FromSeconds(2)).AnyContext();
                Assert.Equal(0, countdown.CurrentCount);
            }
        }

        public virtual async Task WillOnlyReceiveSubscribedMessageType() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AsyncManualResetEvent(false);
                messageBus.Subscribe<SimpleMessageB>(msg => {
                    Assert.True(false, "Received wrong message type.");
                });
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                }).AnyContext();

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(2)).AnyContext();
            }
        }

        public virtual async Task WillReceiveDerivedMessageTypes() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var countdown = new AsyncCountdownEvent(2);
                messageBus.Subscribe<ISimpleMessage>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
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

                await countdown.WaitAsync(TimeSpan.FromSeconds(5)).AnyContext();
            }
        }

        public virtual async Task CanSubscribeToAllMessageTypes() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var countdown = new AsyncCountdownEvent(3);
                messageBus.Subscribe<object>(msg => {
                    countdown.Signal();
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

                await countdown.WaitAsync(TimeSpan.FromSeconds(2)).AnyContext();
            }
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
                var resetEvent = new AsyncAutoResetEvent(false);
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });

                await Assert.ThrowsAsync<TaskCanceledException>(async () => await resetEvent.WaitAsync(TimeSpan.FromMilliseconds(100)).AnyContext()).AnyContext();
            }
        }
    }
}
