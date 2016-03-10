using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Tests.Utility;
using Foundatio.Messaging;
using Xunit;
using Foundatio.Utility;
using Nito.AsyncEx;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public abstract class MessageBusTestBase : TestWithLoggingBase {
        protected MessageBusTestBase(ITestOutputHelper output) : base(output) {}

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
                    _logger.Trace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    _logger.Trace("Set event");
                });

                await Task.Delay(100);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                Trace.WriteLine("Published one...");

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public virtual async Task CanHandleNullMessage() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AsyncManualResetEvent(false);
                messageBus.Subscribe<object>(msg => {
                    throw new ApplicationException();
                });

                await Task.Delay(100);
                await messageBus.PublishAsync<object>(null);
                Trace.WriteLine("Published one...");

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
        
        public virtual async Task CanSendDerivedMessage() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var resetEvent = new AsyncManualResetEvent(false);
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    _logger.Trace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    _logger.Trace("Set event");
                });

                await Task.Delay(100);
                await messageBus.PublishAsync(new DerivedSimpleMessageA {
                    Data = "Hello"
                });
                Trace.WriteLine("Published one...");

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(5));
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
                    if (msg.Count % 500 == 0)
                        _logger.Trace("Got 500 messages");
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                    if (msg.Count % 500 == 0)
                        _logger.Trace("Set 500 events");
                });

                var sw = Stopwatch.StartNew();

                await Run.InParallel(numConcurrentMessages, async i => {
                    await messageBus.PublishAsync(new SimpleMessageA {
                        Data = "Hello",
                        Count = i
                    }, TimeSpan.FromMilliseconds(RandomData.GetInt(0, 300)));
                    if (i % 500 == 0)
                        _logger.Trace("Published 500 messages...");
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
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
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
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
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
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
                });

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(2));
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
                });
                await messageBus.PublishAsync(new SimpleMessageB {
                    Data = "Hello"
                });
                await messageBus.PublishAsync(new SimpleMessageC {
                    Data = "Hello"
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
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
                });
                await messageBus.PublishAsync(new SimpleMessageB {
                    Data = "Hello"
                });
                await messageBus.PublishAsync(new SimpleMessageC {
                    Data = "Hello"
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public virtual async Task WontKeepMessagesWithNoSubscribers() {
            Log.MinimumLevel = LogLevel.Trace;

            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                await Task.Delay(100);
                var resetEvent = new AsyncAutoResetEvent(false);
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });

                await Assert.ThrowsAsync<TaskCanceledException>(async () => await resetEvent.WaitAsync(TimeSpan.FromMilliseconds(100)));
            }
        }
        
        public virtual async Task CanCancelSubscription() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {
                var countdown = new AsyncCountdownEvent(2);

                long messageCount = 0;
                var cancellationTokenSource = new CancellationTokenSource();
                messageBus.Subscribe<SimpleMessageA>(msg => {
                    _logger.Trace("SimpleAMessage received");
                    Interlocked.Increment(ref messageCount);
                    cancellationTokenSource.Cancel();
                    countdown.Signal();
                }, cancellationTokenSource.Token);
                
                messageBus.Subscribe<object>(msg => countdown.Signal());

                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                
                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(1, messageCount);

                countdown = new AsyncCountdownEvent(1);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(1, messageCount);
            }
        }
    }
}
