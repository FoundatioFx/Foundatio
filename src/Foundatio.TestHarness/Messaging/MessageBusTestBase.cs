﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Tests.Extensions;
using Foundatio.Xunit;
using Foundatio.Messaging;
using Foundatio.Utility;
using Xunit;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Messaging {
    public abstract class MessageBusTestBase : TestWithLoggingBase {
        protected MessageBusTestBase(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Debug);
        }

        protected virtual IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null) {
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
                var countdown = new AsyncCountdownEvent(1);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    _logger.LogTrace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    Assert.True(msg.Items.ContainsKey("Test"));
                    countdown.Signal();
                    _logger.LogTrace("Set event");
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello",
                    Items = { { "Test", "Test" } }
                });
                _logger.LogTrace("Published one...");

                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanHandleNullMessageAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(1);
                await messageBus.SubscribeAsync<object>(msg => {
                    countdown.Signal();
                    throw new Exception();
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync<object>(null);
                _logger.LogTrace("Published one...");

                await countdown.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(1, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSendDerivedMessageAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(1);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    _logger.LogTrace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                    _logger.LogTrace("Set event");
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync(new DerivedSimpleMessageA {
                    Data = "Hello"
                });
                _logger.LogTrace("Published one...");
                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSendMappedMessageAsync() {
            var messageBus = GetMessageBus(b => {
                b.TypeNameSerializer = new DefaultTypeNameSerializer(_logger, new Dictionary<string, Type> {{ nameof(SimpleMessageA), typeof(SimpleMessageA) }});
                return b;
            });
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(1);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    _logger.LogTrace("Got message");
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                    _logger.LogTrace("Set event");
                });

                await SystemClock.SleepAsync(100);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });
                _logger.LogTrace("Published one...");
                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, countdown.CurrentCount);
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
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Total Processed {Messages} messages", messages);

                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });

                var sw = Stopwatch.StartNew();
                await Run.InParallelAsync(numConcurrentMessages, async i => {
                    await messageBus.PublishAsync(new SimpleMessageA {
                        Data = "Hello",
                        Count = i
                    }, new MessageOptions { DeliveryDelay = TimeSpan.FromMilliseconds(RandomData.GetInt(0, 100)) });
                    if (i % 500 == 0)
                        _logger.LogTrace("Published 500 messages...");
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
                sw.Stop();

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Processed {Processed} in {Duration:g}", numConcurrentMessages - countdown.CurrentCount, sw.Elapsed);
                Assert.Equal(0, countdown.CurrentCount);
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 50, 5000);
            } finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanSubscribeConcurrentlyAsync() {
            const int iterations = 100;
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(iterations * 10);
                await Run.InParallelAsync(10, i => {
                    return messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                        Assert.Equal("Hello", msg.Data);
                        countdown.Signal();
                    });
                });

                await Run.InParallelAsync(iterations, i => messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" }));
                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
            }
            finally {
                await CleanupMessageBusAsync(messageBus);
            }
        }

        public virtual async Task CanReceiveMessagesConcurrentlyAsync() {
            const int iterations = 100;
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            var messageBuses = new List<IMessageBus>(10);
            try {
                var countdown = new AsyncCountdownEvent(iterations * 10);
                await Run.InParallelAsync(10, async i => {
                    var bus = GetMessageBus();
                    await bus.SubscribeAsync<SimpleMessageA>(msg => {
                        Assert.Equal("Hello", msg.Data);
                        countdown.Signal();
                    });

                    messageBuses.Add(bus);
                });
                var subscribe = Run.InParallelAsync(iterations,
                    i => {
                        SystemClock.Sleep(RandomData.GetInt(0, 10));
                        return messageBuses.Random().SubscribeAsync<NeverPublishedMessage>(msg => Task.CompletedTask);
                    });

                var publish = Run.InParallelAsync(iterations + 3, i => {
                    return i switch {
                        1 => messageBus.PublishAsync(new DerivedSimpleMessageA { Data = "Hello" }),
                        2 => messageBus.PublishAsync(new Derived2SimpleMessageA { Data = "Hello" }),
                        3 => messageBus.PublishAsync(new Derived3SimpleMessageA { Data = "Hello" }),
                        4 => messageBus.PublishAsync(new Derived4SimpleMessageA { Data = "Hello" }),
                        5 => messageBus.PublishAsync(new Derived5SimpleMessageA { Data = "Hello" }),
                        6 => messageBus.PublishAsync(new Derived6SimpleMessageA { Data = "Hello" }),
                        7 => messageBus.PublishAsync(new Derived7SimpleMessageA { Data = "Hello" }),
                        8 => messageBus.PublishAsync(new Derived8SimpleMessageA { Data = "Hello" }),
                        9 => messageBus.PublishAsync(new Derived9SimpleMessageA { Data = "Hello" }),
                        10 => messageBus.PublishAsync(new Derived10SimpleMessageA { Data = "Hello" }),
                        iterations + 1 => messageBus.PublishAsync(new { Data = "Hello" }),
                        iterations + 2 => messageBus.PublishAsync(new SimpleMessageC { Data = "Hello" }),
                        iterations + 3 => messageBus.PublishAsync(new SimpleMessageB { Data = "Hello" }),
                        _ => messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" }),
                    };
                });

                await Task.WhenAll(subscribe, publish);
                await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
            } finally {
                foreach (var mb in messageBuses)
                    await CleanupMessageBusAsync(mb);

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
                var countdown = new AsyncCountdownEvent(4);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
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
                var countdown = new AsyncCountdownEvent(1);
                await messageBus.SubscribeAsync<SimpleMessageB>(msg => {
                    Assert.True(false, "Received wrong message type");
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

        public virtual async Task CanSubscribeToRawMessagesAsync() {
            var messageBus = GetMessageBus();
            if (messageBus == null)
                return;

            try {
                var countdown = new AsyncCountdownEvent(3);
                await messageBus.SubscribeAsync(msg => {
                    Assert.True(msg.Type.Contains(nameof(SimpleMessageA))
                        || msg.Type.Contains(nameof(SimpleMessageB))
                        || msg.Type.Contains(nameof(SimpleMessageC)));
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
                var countdown = new AsyncCountdownEvent(1);
                await messageBus.PublishAsync(new SimpleMessageA {
                    Data = "Hello"
                });

                await SystemClock.SleepAsync(100);
                var resetEvent = new AsyncAutoResetEvent(false);
                await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                });
                
                await countdown.WaitAsync(TimeSpan.FromMilliseconds(100));
                Assert.Equal(1, countdown.CurrentCount);
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
                    _logger.LogTrace("SimpleAMessage received");
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
