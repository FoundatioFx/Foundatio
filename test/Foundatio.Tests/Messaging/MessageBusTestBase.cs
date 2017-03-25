using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Tests.Extensions;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Messaging;
using Foundatio.Utility;
using Xunit;
using Nito.AsyncEx;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public abstract class MessageBusTestBase : TestWithLoggingBase {
        protected MessageBusTestBase(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Debug);
        }

        protected virtual IMessageBus GetMessageBus() {
            return null;
        }

        protected virtual Task CleanupMessageBusAsync(IMessageBus messageBus) {
            messageBus?.Dispose();
            return Task.CompletedTask;
        }

        public virtual async Task CanSendMessageAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var resetEvent = new AsyncManualResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    _logger.Trace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    _logger.Trace("Set event");
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                _logger.Trace("Published one...");

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(5));
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanHandleNullMessageAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var resetEvent = new AsyncManualResetEvent(false);
                await messageBus.SubscribeAsync<object>(msg => {
                    resetEvent.Set();
                    throw new Exception();
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync<object>(null);
                _logger.Trace("Published one...");

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resetEvent.WaitAsync(TimeSpan.FromSeconds(1)));
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSendDerivedMessageAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var resetEvent = new AsyncManualResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    _logger.Trace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                    _logger.Trace("Set event");
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync(new DerivedSimpleMessageA {
                    Data = "Hello"
                });
                _logger.Trace("Published one...");

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(5));
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSendDelayedMessageAsync() {
            const int numConcurrentMessages = 1000;
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(numConcurrentMessages);

                int messages = 0;
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    if (++messages % 50 == 0)
                        _logger.Trace($"Totoal Processed {messages} messages");

                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });

                var sw = Stopwatch.StartNew();
                await Run.InParallelAsync(numConcurrentMessages, async i => {
                    await messageBus.PublishAsync(new SimpleMessageA {
                        Data = "Hello",
                        Count = i
                    }, TimeSpan.FromMilliseconds(RandomData.GetInt(0, 100)));
                    if (i % 500 == 0)
                        _logger.Trace("Published 500 messages...");
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
                sw.Stop();

                _logger.Trace($"Processed {numConcurrentMessages - countdown.CurrentCount} in {sw.ElapsedMilliseconds}ms");
                Assert.Equal(0, countdown.CurrentCount);
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 70, 5000);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSendMessageToMultipleSubscribersAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(3);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanTolerateSubscriberFailureAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(2);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    throw new Exception();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task WillOnlyReceiveSubscribedMessageTypeAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var resetEvent = new AsyncManualResetEvent(false);
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

                await resetEvent.WaitAsync(TimeSpan.FromSeconds(2));
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task WillReceiveDerivedMessageTypesAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(2);
                await messageBus.SubscribeAsync<ISimpleMessage>(msg => {
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
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSubscribeToAllMessageTypesAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(3);
                await messageBus.SubscribeAsync<object>(msg => {
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
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task WontKeepMessagesWithNoSubscribersAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                await SystemClock.SleepAsync(100);
                var resetEvent = new AsyncAutoResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    resetEvent.Set();
                });

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resetEvent.WaitAsync(TimeSpan.FromMilliseconds(100)));
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanCancelSubscriptionAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(2);

                long messageCount = 0;
                var cancellationTokenSource = new CancellationTokenSource();
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    _logger.Trace("SimpleAMessage received");
                    Interlocked.Increment(ref messageCount);
                    cancellationTokenSource.Cancel();
                    countdown.Signal();
                }, cancellationTokenSource.Token);

                await messageBus.SubscribeAsync<object>(msg => countdown.Signal());

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
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanReceiveFromMultipleSubscribersAsync() {
            var messageBus1 = GetMessageBus();
            if (messageBus1 == null)
                return;

            try {
                var countdown1 = new AsyncCountdownEvent(1);
                await messageBus1.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown1.Signal();
                });

                var messageBus2 = GetMessageBus();
                try {
                    var countdown2 = new AsyncCountdownEvent(1);
                    await messageBus2.SubscribeAsync<SimpleMessageA>(msg => {
                        Assert.Equal("Hello", msg.Data);
                        countdown2.Signal();
                    });

                    await messageBus1.PublishAsync(new SimpleMessageA {
                        Data = "Hello"
                    });

                    await countdown1.WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(0, countdown1.CurrentCount);
                    await countdown2.WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(0, countdown2.CurrentCount);
                } finally {
                    await CleanupMessageBusAsync(messageBus2);
                }
            } finally {
                await CleanupMessageBusAsync(messageBus1);
            }
        }

        public virtual void CanDisposeWithNoSubscribersOrPublishers() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            using (messageBus) {}
        }
    }
}
