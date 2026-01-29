using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

public abstract class MessageBusTestBase : TestWithLoggingBase
{
    protected MessageBusTestBase(ITestOutputHelper output) : base(output)
    {
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Debug);
    }

    protected virtual IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null)
    {
        return null;
    }

    protected virtual Task CleanupMessageBusAsync(IMessageBus messageBus)
    {
        messageBus?.Dispose();
        return Task.CompletedTask;
    }

    public virtual async Task CanUseMessageOptionsAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        using var metrics = new InMemoryMetrics(FoundatioDiagnostics.Meter.Name, _logger);

        try
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == FoundatioDiagnostics.ActivitySource.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => _logger.LogInformation("Start: {ActivityDisplayName}", activity.DisplayName),
                ActivityStopped = activity => _logger.LogInformation("Stop: {ActivityDisplayName}", activity.DisplayName),
            };

            ActivitySource.AddActivityListener(listener);

            using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Parent");
            Assert.NotNull(activity);
            Assert.NotNull(Activity.Current);
            Assert.Equal(Activity.Current, activity);

            var countdown = new AsyncCountdownEvent(1);
            await messageBus.SubscribeAsync<IMessage<SimpleMessageA>>(msg =>
            {
                _logger.LogTrace("Got message");

                Assert.Equal("Hello", msg.Body.Data);
                Assert.True(msg.Body.Items.ContainsKey("Test"));

                Assert.Equal(activity.Id, msg.CorrelationId);
                Assert.Equal(Activity.Current.ParentId, activity.Id);
                Assert.Single(msg.Properties);
                Assert.Contains(msg.Properties, i => i.Key == "hey" && i.Value.ToString() == "now");
                countdown.Signal();
                _logger.LogTrace("Set event");
            });

            await Task.Delay(1000);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello",
                Items = { { "Test", "Test" } }
            }, new MessageOptions
            {
                Properties = new Dictionary<string, string>
                {
                    { "hey", "now" }
                }
            }, TestCancellationToken);
            _logger.LogTrace("Published one...");

            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSendMessageAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(1);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                _logger.LogTrace("Got message");
                Assert.Equal("Hello", msg.Data);
                Assert.True(msg.Items.ContainsKey("Test"));
                countdown.Signal();
                _logger.LogTrace("Set event");
            }, TestCancellationToken);

            await Task.Delay(100);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello",
                Items = { { "Test", "Test" } }
            }, cancellationToken: TestCancellationToken);
            _logger.LogTrace("Published one...");

            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanHandleNullMessageAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            // Publishing null should throw ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await messageBus.PublishAsync<object>(null));
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSendDerivedMessageAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(1);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                _logger.LogTrace("Got message");
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
                _logger.LogTrace("Set event");
            }, TestCancellationToken);

            await Task.Delay(100);
            await messageBus.PublishAsync(new DerivedSimpleMessageA
            {
                Data = "Hello"
            });
            _logger.LogTrace("Published one...");
            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSendMappedMessageAsync()
    {
        using var messageBus = GetMessageBus(b =>
        {
            b.MessageTypeMappings.Add(nameof(SimpleMessageA), typeof(SimpleMessageA));
            return b;
        });
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(1);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                _logger.LogTrace("Got message");
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
                _logger.LogTrace("Set event");
            }, TestCancellationToken);

            await Task.Delay(100);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            _logger.LogTrace("Published one...");
            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSendDelayedMessageAsync()
    {
        const int numConcurrentMessages = 1000;
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            // Arrange
            var countdown = new AsyncCountdownEvent(numConcurrentMessages);
            int messages = 0;
            int optionsVerifiedCount = 0;

            await messageBus.SubscribeAsync<IMessage<SimpleMessageA>>(msg =>
            {
                Assert.Equal("Hello", msg.Body.Data);

                // Verify options are preserved through delayed delivery
                if (!String.IsNullOrEmpty(msg.CorrelationId) && msg.CorrelationId.StartsWith("correlation-"))
                {
                    Assert.True(msg.Properties.TryGetValue("TestKey", out var value));
                    Assert.Equal("TestValue", value);
                    Interlocked.Increment(ref optionsVerifiedCount);
                }

                if (Interlocked.Increment(ref messages) % 50 == 0)
                    _logger.LogTrace("Total Processed {Messages} messages", messages);

                countdown.Signal();
            });

            // Act
            var sw = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(1, numConcurrentMessages), async (i, _) =>
            {
                await messageBus.PublishAsync(new SimpleMessageA
                {
                    Data = "Hello",
                    Count = i
                }, new MessageOptions
                {
                    DeliveryDelay = TimeSpan.FromMilliseconds(RandomData.GetInt(0, 100)),
                    CorrelationId = $"correlation-{i}",
                    Properties = new Dictionary<string, string> { { "TestKey", "TestValue" } }
                }, TestCancellationToken);

                if (i % 500 == 0)
                    _logger.LogTrace("Published 500 messages...");
            });

            await countdown.WaitAsync(TimeSpan.FromSeconds(30));
            sw.Stop();

            // Assert
            _logger.LogTrace("Processed {Processed} in {Duration:g}", numConcurrentMessages - countdown.CurrentCount, sw.Elapsed);
            Assert.Equal(0, countdown.CurrentCount);
            Assert.InRange(sw.Elapsed.TotalMilliseconds, 50, 30000);
            Assert.Equal(numConcurrentMessages, optionsVerifiedCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSubscribeConcurrentlyAsync()
    {
        const int iterations = 100;
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(iterations * 10);
            await Parallel.ForEachAsync(Enumerable.Range(1, 10), async (_, ct) =>
            {
                await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
                {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                }, cancellationToken: ct);
            });

            await Parallel.ForEachAsync(Enumerable.Range(1, iterations), async (_, _) => await messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken));
            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanReceiveMessagesConcurrentlyAsync()
    {
        const int iterations = 100;
        var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        var messageBuses = new List<IMessageBus>(10);
        try
        {
            var countdown = new AsyncCountdownEvent(iterations * 10);
            await Parallel.ForEachAsync(Enumerable.Range(1, 10), async (_, ct) =>
            {
                var bus = GetMessageBus();
                await bus.SubscribeAsync<SimpleMessageA>(msg =>
                {
                    Assert.Equal("Hello", msg.Data);
                    countdown.Signal();
                }, cancellationToken: ct);

                messageBuses.Add(bus);
            });

            var subscribe = Parallel.ForEachAsync(Enumerable.Range(1, iterations), async (i, ct) =>
            {
                await Task.Delay(RandomData.GetInt(0, 10), ct);
                await messageBuses.Random().SubscribeAsync<NeverPublishedMessage>(msg => Task.CompletedTask, cancellationToken: ct);
            });

            var publish = Parallel.ForEachAsync(Enumerable.Range(1, iterations + 3), async (i, _) =>
            {
                await (i switch
                {
                    1 => messageBus.PublishAsync(new DerivedSimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    2 => messageBus.PublishAsync(new Derived2SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    3 => messageBus.PublishAsync(new Derived3SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    4 => messageBus.PublishAsync(new Derived4SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    5 => messageBus.PublishAsync(new Derived5SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    6 => messageBus.PublishAsync(new Derived6SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    7 => messageBus.PublishAsync(new Derived7SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    8 => messageBus.PublishAsync(new Derived8SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    9 => messageBus.PublishAsync(new Derived9SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    10 => messageBus.PublishAsync(new Derived10SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    iterations + 1 => messageBus.PublishAsync(new { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    iterations + 2 => messageBus.PublishAsync(new SimpleMessageC { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    iterations + 3 => messageBus.PublishAsync(new SimpleMessageB { Data = "Hello" }, cancellationToken: TestCancellationToken),
                    _ => messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken)
                });
            });

            await Task.WhenAll(subscribe, publish);
            await countdown.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            foreach (var mb in messageBuses)
                await CleanupMessageBusAsync(mb);

            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSendMessageToMultipleSubscribersAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(3);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanTolerateSubscriberFailureAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(4);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg => throw new Exception());
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task WillOnlyReceiveSubscribedMessageTypeAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(1);
            await messageBus.SubscribeAsync<SimpleMessageB>(msg =>
            {
                Assert.Fail("Received wrong message type");
            }, TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task WillReceiveDerivedMessageTypesAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(2);
            await messageBus.SubscribeAsync<ISimpleMessage>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            });
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageB
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageC
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSubscribeToRawMessagesAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(3);
            await messageBus.SubscribeAsync(msg =>
            {
                Assert.True(msg.Type.Contains(nameof(SimpleMessageA))
                            || msg.Type.Contains(nameof(SimpleMessageB))
                            || msg.Type.Contains(nameof(SimpleMessageC)));
                countdown.Signal();
            });
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageB
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageC
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanSubscribeToAllMessageTypesAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(3);
            await messageBus.SubscribeAsync<object>(msg =>
            {
                countdown.Signal();
            }, TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageB
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageC
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task WontKeepMessagesWithNoSubscribersAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(1);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await Task.Delay(100);
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown.Signal();
            }, TestCancellationToken);

            await Assert.ThrowsAsync<TimeoutException>(async () => await countdown.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(1, countdown.CurrentCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanCancelSubscriptionAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            var countdown = new AsyncCountdownEvent(2);

            long messageCount = 0;
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
            await messageBus.SubscribeAsync<SimpleMessageA>(async msg =>
            {
                _logger.LogTrace("SimpleAMessage received");
                Interlocked.Increment(ref messageCount);
                await cancellationTokenSource.CancelAsync();
                countdown.Signal();
            }, cancellationTokenSource.Token);

            // NOTE: This subscriber will not be canceled.
            await messageBus.SubscribeAsync<object>(_ => countdown.Signal(), TestCancellationToken);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, countdown.CurrentCount);
            Assert.Equal(1, messageCount);

            countdown.AddCount(1);
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "Hello"
            }, cancellationToken: TestCancellationToken);

            await countdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, countdown.CurrentCount);
            Assert.Equal(1, messageCount);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task CanReceiveFromMultipleSubscribersAsync()
    {
        using var messageBus1 = GetMessageBus();
        if (messageBus1 == null)
            return;

        try
        {
            var countdown1 = new AsyncCountdownEvent(1);
            await messageBus1.SubscribeAsync<SimpleMessageA>(msg =>
            {
                Assert.Equal("Hello", msg.Data);
                countdown1.Signal();
            }, TestCancellationToken);

            using var messageBus2 = GetMessageBus();
            try
            {
                var countdown2 = new AsyncCountdownEvent(1);
                await messageBus2.SubscribeAsync<SimpleMessageA>(msg =>
                {
                    Assert.Equal("Hello", msg.Data);
                    countdown2.Signal();
                }, TestCancellationToken);

                await messageBus1.PublishAsync(new SimpleMessageA
                {
                    Data = "Hello"
                }, cancellationToken: TestCancellationToken);

                await countdown1.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, countdown1.CurrentCount);
                await countdown2.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, countdown2.CurrentCount);
            }
            finally
            {
                await CleanupMessageBusAsync(messageBus2);
            }
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus1);
        }
    }

    public virtual void CanDisposeWithNoSubscribersOrPublishers()
    {
        using var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        using (messageBus)
        {
            // Empty using statement to ensure Dispose is called
        }
    }

    public virtual async Task CanHandlePoisonedMessageAsync()
    {
        using var messageBus = GetMessageBus();
        if (messageBus is null)
            return;

        long handlerInvocations = 0;

        try
        {
            await messageBus.SubscribeAsync<SimpleMessageA>(_ =>
            {
                _logger.LogTrace("SimpleAMessage received");
                Interlocked.Increment(ref handlerInvocations);
                throw new Exception("Poisoned message");
            });

            await messageBus.PublishAsync(new SimpleMessageA(), cancellationToken: TestCancellationToken);
            _logger.LogTrace("Published one...");

            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.InRange(handlerInvocations, 1, 5);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task PublishAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync()
    {
        var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            // Act & Assert - cancelled token should throw OperationCanceledException, not MessageBusException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await messageBus.PublishAsync(new SimpleMessageA(), cancellationToken: new CancellationToken(true)));
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task PublishAsync_WithDelayedMessageAndDisposeBeforeDelivery_DiscardsMessageAsync()
    {
        // Arrange
        var messageReceived = new AsyncAutoResetEvent(false);
        var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
            {
                _logger.LogTrace("Got message - this should NOT happen");
                messageReceived.Set();
            }, TestCancellationToken);

            // Act - publish with short delay, then dispose immediately before delay expires
            await messageBus.PublishAsync(new SimpleMessageA
            {
                Data = "ShouldBeDiscarded"
            }, new MessageOptions { DeliveryDelay = TimeSpan.FromSeconds(1) }, TestCancellationToken);

            _logger.LogTrace("Published delayed message, disposing immediately...");
            messageBus.Dispose();
            messageBus = null; // Mark as disposed to skip cleanup

            // Assert - wait slightly longer than the delay; message should NOT be delivered
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => messageReceived.WaitAsync(cts.Token));
        }
        finally
        {
            if (messageBus != null)
                await CleanupMessageBusAsync(messageBus);
        }
    }

    public virtual async Task SubscribeAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync()
    {
        var messageBus = GetMessageBus();
        if (messageBus == null)
            return;

        try
        {
            // Act & Assert - cancelled token should throw OperationCanceledException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await messageBus.SubscribeAsync<SimpleMessageA>(_ => { }, cancellationToken: new CancellationToken(true)));
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

}
