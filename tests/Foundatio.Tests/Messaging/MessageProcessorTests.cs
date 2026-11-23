using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Leases, settlement and lifecycle of the processors that run message handlers.</summary>
public class MessageProcessorTests : TestWithLoggingBase
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(1);

    // A body larger than half the buffered-bytes cap, so one fits within it and two do not.
    private const int LargeBodyLength = (int)(ReceiveAhead.MaxBufferedBytes / 2) + 1024 * 1024;
    private static readonly RetryPolicy LongRetries = new() { Delay = RetryPolicy.Fixed(TimeSpan.FromMinutes(1)) };
    private readonly InMemoryMessageTransport _transport = new();

    public MessageProcessorTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task OrderedGroupBatch_WhenEarlierMessagesOutlastTheLease_LaterMessagesKeepTheirLease()
    {
        var calls = new ConcurrentQueue<(int Id, int Attempt)>();
        int running = 0, overlapped = 0;
        await using (var producer = await StartAsync(_transport, _ => { }))
            await producer.Client.SendBatchAsync(Enumerable.Range(1, 3).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "customer-1" }, TestCancellationToken);

        await using var consumer = await StartAsync(_transport, m => m.AddConsumer<GroupedCommand>(async (message, context, _) =>
        {
            if (Interlocked.Increment(ref running) > 1)
                Interlocked.Increment(ref overlapped);

            calls.Enqueue((message.Id, context.Attempt));
            await Task.Delay(700, CancellationToken.None);
            Interlocked.Decrement(ref running);
        }, new HandlerOptions { LeaseDuration = ShortLease, MaxConcurrency = 8 }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal([(1, 1), (2, 1), (3, 1)], calls);
        Assert.Equal(0, overlapped);
    }

    [Fact]
    public async Task LeaseRenewal_WhenARenewalFails_RetriesBeforeTheLeaseExpires()
    {
        int failures = 1, handled = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnRenew = async (_, ct) =>
            {
                if (Interlocked.Decrement(ref failures) >= 0)
                {
                    await Task.Delay(200, ct);
                    throw new IOException("Transient renewal failure");
                }
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None);
        }, new HandlerOptions { LeaseDuration = ShortLease, MaxConcurrency = 4 }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.True(failures < 0, "The renewal never failed, so the test did not exercise a failed renewal.");
        Assert.Equal(1, Volatile.Read(ref handled));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    public async Task LeaseRenewal_WhenOnlyTheFirstRenewalHangs_RetriesItBeforeTheLeaseIsLost(int leaseSeconds)
    {
        var lease = TimeSpan.FromSeconds(leaseSeconds);
        int renewals = 0, cancelled = 0, completed = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnRenew = async (_, ct) =>
            {
                if (Interlocked.Increment(ref renewals) == 1)
                    await Task.Delay(Timeout.Infinite, ct);
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(lease * 1.2, ct);
                Interlocked.Increment(ref completed);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
        }, new HandlerOptions { LeaseDuration = lease, MaxConcurrency = 1, RetryPolicy = new RetryPolicy { MaxAttempts = 1 } }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(lease * 4, TestCancellationToken));
        Assert.Equal(0, Volatile.Read(ref cancelled));
        Assert.Equal(1, Volatile.Read(ref completed));
        Assert.True(Volatile.Read(ref renewals) >= 2, $"The hung renewal was never retried ({Volatile.Read(ref renewals)} renewal calls).");
    }

    [Fact]
    public async Task ProcessSequence_WhenTheLeaseTimerRunsLate_DoesNotStartAHandlerPastTheLostPoint()
    {
        // The clock jumps past the point where the waiting message's lease counts as lost, as a starved thread pool
        // would make the renewer's timer run late: it must not be handed to its handler, though the timer never marked it.
        var clock = new JumpingTimeProvider();
        await using var transport = new InMemoryMessageTransport(clock);
        var lease = TimeSpan.FromSeconds(20);
        var attempts = new ConcurrentQueue<(int Id, int Attempt)>();
        await using (var producer = await StartAsync(transport, _ => { }))
            await producer.Client.SendBatchAsync([new GroupedCommand(1), new GroupedCommand(2)], new SendOptions { GroupKey = "g" }, TestCancellationToken);

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>((message, context, _) =>
        {
            attempts.Enqueue((message.Id, context.Attempt));
            if (message.Id == 1)
                clock.Jump(lease * 0.9);

            return Task.CompletedTask;
        }, new HandlerOptions { LeaseDuration = lease, MaxConcurrency = 2 }), s => s.AddSingleton<TimeProvider>(clock));

        Assert.True(await transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal([(1, 1), (2, 2)], attempts);
    }

    [Fact]
    public async Task LeaseRenewal_WhenNoRenewalReachesTheBrokerBeforeTheLeaseEnds_CancelsTheHandlerBeforeAnotherConsumerGetsTheMessage()
    {
        // Consumer A is cut off from the broker after it received the message (a partition, or latency past the lease):
        // every renewal fails. The broker's lease ends and consumer B, which still reaches the broker, receives the
        // message again. A's handler must have been stopped by then, as the job worker stops a job it cannot renew.
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long bStartedAt = 0, aEndedAt = 0;
        int aCancelled = 0;
        var partitioned = new InterceptingMessageTransport(_transport)
        {
            OnRenew = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                throw new IOException("No route to the broker");
            }
        };

        await using var a = await StartAsync(partitioned, m => m.AddConsumer<RenewedCommand>(async (_, _, ct) =>
        {
            aStarted.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref aCancelled, 1);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref aEndedAt, Stopwatch.GetTimestamp());
                aEnded.TrySetResult();
            }
        }, new HandlerOptions { LeaseDuration = ShortLease, MaxConcurrency = 1 }));

        await a.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        await using var b = await StartAsync(_transport, m => m.AddConsumer<RenewedCommand>((_, _, _) =>
        {
            Interlocked.CompareExchange(ref bStartedAt, Stopwatch.GetTimestamp(), 0);
            bStarted.TrySetResult();
            return Task.CompletedTask;
        }, new HandlerOptions { LeaseDuration = ShortLease, MaxConcurrency = 1 }));

        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await aEnded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        double overlapMs = Stopwatch.GetElapsedTime(Volatile.Read(ref bStartedAt), Volatile.Read(ref aEndedAt)).TotalMilliseconds;
        Assert.True(overlapMs <= 0, $"A's handler was still running {overlapMs:F0} ms after B received the message again");
        Assert.Equal(1, Volatile.Read(ref aCancelled));
    }

    [Fact]
    public async Task LeaseRenewal_WithoutLeaseExpiration_WhenRenewalsKeepFailing_LetsTheHandlerFinish()
    {
        // Without lease expiration (Kafka, RabbitMQ) a lease never runs out, so failed renewals alone never cancel the handler.
        await using var inner = new InMemoryMessageTransport(new InMemoryMessageTransport().Capabilities with { LeaseExpiration = false });
        int renewals = 0, cancelled = 0, handled = 0;
        var transport = new InterceptingMessageTransport(inner)
        {
            OnRenew = (_, _) =>
            {
                Interlocked.Increment(ref renewals);
                throw new IOException("The broker cannot be reached");
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
                Interlocked.Increment(ref handled);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
        }, new HandlerOptions { LeaseDuration = ShortLease, MaxConcurrency = 1 }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        Assert.True(await inner.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.True(Volatile.Read(ref renewals) > 1, "No renewal failed, so the test did not exercise failing renewals.");
        Assert.Equal(0, Volatile.Read(ref cancelled));
        Assert.Equal(1, Volatile.Read(ref handled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_WhenItThrowsBeforeSettling_AbandonsTheDelivery(bool inOrder)
    {
        // Without lease expiration an unsettled delivery never comes back, so handling that throws must release it.
        await using var transport = new InMemoryMessageTransport(new InMemoryMessageTransport().Capabilities with { LeaseExpiration = false });
        var source = MessageSource.Queue("throwing");
        await transport.SendAsync(MessageDestination.Queue("throwing"), [new OutgoingMessage { MessageId = "m1", Body = new byte[] { 1 } }], TestCancellationToken);
        var handler = new ThrowOnceDeliveryHandler();
        var settings = new MessageProcessorSettings { MaxConcurrency = 2, LeaseDuration = ShortLease, ReplaceTimeout = TimeSpan.FromSeconds(5), Description = "test", InOrder = inOrder };
        await using var processor = new MessageProcessor(transport, source, settings, handler, new ProcessingStatusTracker(TimeProvider.System), TimeProvider.System, Log);
        await processor.StartAsync(TestCancellationToken);

        Assert.True(await transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal(2, Volatile.Read(ref handler.Calls));
        Assert.Equal(["retry"], handler.Released);
    }

    [Fact]
    public async Task LeaseRenewal_WhenTheBrokerDecidesAShorterLease_RenewsBeforeItEnds()
    {
        int handled = 0, renewals = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            BrokerLeaseDuration = ShortLease,
            OnRenew = (_, _) =>
            {
                Interlocked.Increment(ref renewals);
                return Task.CompletedTask;
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
        }, new HandlerOptions { LeaseDuration = TimeSpan.FromMinutes(5), MaxConcurrency = 4 }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal(1, Volatile.Read(ref handled));
        Assert.InRange(Volatile.Read(ref renewals), 3, 30);
    }

    [Fact]
    public async Task Settle_WhileARenewalIsInFlight_WaitsForTheRenewalToStopFirst()
    {
        int renewing = 0, settledWhileRenewing = 0, renewals = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnRenew = async (_, ct) =>
            {
                Interlocked.Increment(ref renewals);
                Interlocked.Increment(ref renewing);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                finally
                {
                    Interlocked.Decrement(ref renewing);
                }
            },
            OnSettle = (_, _) =>
            {
                if (Volatile.Read(ref renewing) > 0)
                    Interlocked.Increment(ref settledWhileRenewing);
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, _, _) =>
        {
            while (Volatile.Read(ref renewing) == 0)
                await Task.Delay(10, CancellationToken.None);
        }, new HandlerOptions { LeaseDuration = ShortLease }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.True(Volatile.Read(ref renewals) > 0);
        Assert.Equal(0, Volatile.Read(ref settledWhileRenewing));
    }

    [Fact]
    public async Task DeadLetter_WhenTheTransportCannotDeadLetter_BacksOffWithoutRunningTheHandlerAgainAndReportsIt()
    {
        bool deadLettersFail = true;
        int deadLetterAttempts = 0, handled = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnDeadLetter = (_, _) =>
            {
                Interlocked.Increment(ref deadLetterAttempts);
                return Volatile.Read(ref deadLettersFail) ? throw new IOException("Access denied to the dead-letter queue") : Task.CompletedTask;
            }
        };
        var policy = new RetryPolicy { MaxAttempts = 2, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<PoisonCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            throw new InvalidOperationException("still broken");
        }, new HandlerOptions { RetryPolicy = policy }));
        var status = consumer.Services.GetRequiredService<MessagingStatus>();
        var source = MessageSource.Queue(QueueName<PoisonCommand>());

        await consumer.Client.SendAsync(new PoisonCommand(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref deadLetterAttempts) >= 2, TimeSpan.FromSeconds(15));

        Assert.Equal(2, Volatile.Read(ref handled));
        var failing = Assert.Single(status.GetProcessors());
        Assert.NotNull(failing.FailingSince);
        Assert.Contains("Access denied to the dead-letter queue", failing.LastError);

        Volatile.Write(ref deadLettersFail, false);
        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 1
            && status.GetProcessors().Single().FailingSince is null, TimeSpan.FromSeconds(15));

        Assert.Equal(2, Volatile.Read(ref handled));
        Assert.Equal(0, _transport.GetMessageCount(source));
    }

    [Fact]
    public async Task DeadLetter_WithAReasonOverTheLimit_TruncatesTheReason()
    {
        await using var consumer = await StartAsync(_transport, m => m
            .AddConsumer<PoisonCommand>((_, _, _) => throw new InvalidOperationException(new string('é', 5000)), new HandlerOptions { RetryPolicy = RetryPolicy.None })
            .AddConsumer<RenewedCommand>((_, context, _) =>
            {
                context.DeadLetter(new string('x', 5000));
                return Task.CompletedTask;
            }));

        await consumer.Client.SendAsync(new PoisonCommand(1), cancellationToken: TestCancellationToken);
        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));

        foreach (var source in new[] { MessageSource.Queue(QueueName<PoisonCommand>()), MessageSource.Queue(QueueName<RenewedCommand>()) })
        {
            await using var receiver = await _transport.CreateReceiverAsync(MessageSource.DeadLetter(source), new ReceiverOptions(), TestCancellationToken);
            var deadLetter = Assert.Single(await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken));
            string reason = deadLetter.Headers[KnownHeaders.DeadLetterReason];
            Assert.InRange(Encoding.UTF8.GetByteCount(reason), 100, 1024);
        }
    }

    [Fact]
    public async Task RetryHold_WhenTheTransportCannotDelayRetries_GivesTheSlotToOtherMessagesAndKeepsTheLease()
    {
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        int renewals = 0;
        var settled = new ConcurrentQueue<(string Operation, string MessageId)>();
        var transport = new InterceptingMessageTransport(_transport, WithoutRetryDelays())
        {
            OnRenew = (message, _) =>
            {
                if (message.MessageId == "held")
                    Interlocked.Increment(ref renewals);

                return Task.CompletedTask;
            },
            OnSettle = (operation, message) => settled.Enqueue((operation, message.MessageId))
        };
        var handled = new ConcurrentQueue<(int Id, int Attempt)>();
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, context, _) =>
        {
            handled.Enqueue((message.Id, context.Attempt));
            return message.Id == 0 ? throw new InvalidOperationException("transient") : Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 1, LeaseDuration = ShortLease, RetryPolicy = LongRetries }));
        var status = consumer.Services.GetRequiredService<MessagingStatus>();

        await consumer.Client.SendAsync(new RenewedCommand(0), new SendOptions { MessageId = "held" }, TestCancellationToken);
        await consumer.Client.SendBatchAsync(Enumerable.Range(1, 3).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => handled.Count == 4 && Volatile.Read(ref renewals) >= 3, TimeSpan.FromSeconds(10));

        Assert.Equal([(0, 1), (1, 1), (2, 1), (3, 1)], handled);
        Assert.DoesNotContain(settled, s => s.MessageId == "held");
        Assert.Equal(1, _transport.GetMessageCount(source));
        Assert.Equal(1, status.GetProcessors().Single().Active);

        var stopwatch = Stopwatch.StartNew();
        await consumer.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Stopping took {stopwatch.Elapsed}; a held retry is released at once, not waited out.");
        Assert.Contains(("abandon", "held"), settled);
        Assert.Equal(1, _transport.GetMessageCount(source));
    }

    [Fact]
    public async Task RetryHold_WhenTheHoldingSetIsFull_HoldsFurtherRetriesInTheirSlots()
    {
        const int maxConcurrency = 2, healthy = 3;
        const int failing = maxConcurrency * (RetryHolds.HoldsPerSlot + 1);
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        var transport = new InterceptingMessageTransport(_transport, WithoutRetryDelays());
        int failures = 0, succeeded = 0;
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, _, _) =>
        {
            if (message.Id < failing)
            {
                Interlocked.Increment(ref failures);
                throw new InvalidOperationException("transient");
            }

            Interlocked.Increment(ref succeeded);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = maxConcurrency, RetryPolicy = LongRetries }));

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, failing + healthy).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref failures) == failing, TimeSpan.FromSeconds(10));

        var stats = await _transport.GetStatsAsync(source, TestCancellationToken);
        Assert.Equal(((long)healthy, (long)failing), (stats!.Queued, stats.InFlight));
        Assert.Equal(0, Volatile.Read(ref succeeded));

        var stopwatch = Stopwatch.StartNew();
        await consumer.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Stopping took {stopwatch.Elapsed}; held retries are released at once, not waited out.");
        stats = await _transport.GetStatsAsync(source, TestCancellationToken);
        Assert.Equal(((long)(failing + healthy), 0L), (stats!.Queued, stats.InFlight));
    }

    [Fact]
    public async Task RetryHold_InAnOrderedGroup_KeepsTheGroupsLaterMessagesBehindItAndFreesTheirSlots()
    {
        var transport = new InterceptingMessageTransport(_transport, WithoutRetryDelays());
        await PrefillAsync(Enumerable.Range(1, 3).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "customer-1" });
        await PrefillAsync(Enumerable.Range(10, 3).Select(i => new GroupedCommand(i)));
        var calls = new ConcurrentQueue<(int Id, int Attempt)>();
        var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>((message, context, _) =>
        {
            calls.Enqueue((message.Id, context.Attempt));
            return message.Id == 1 && context.Attempt == 1 ? throw new InvalidOperationException("transient") : Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 3, RetryPolicy = LongRetries }));

        await WaitForAsync(() => calls.Count == 4, TimeSpan.FromSeconds(10));
        Assert.Equal([(1, 1), (10, 1), (11, 1), (12, 1)], calls.OrderBy(c => c.Id));

        await consumer.DisposeAsync();
        calls.Clear();
        await using var next = await StartAsync(_transport, m => m.AddConsumer<GroupedCommand>((message, context, _) =>
        {
            calls.Enqueue((message.Id, context.Attempt));
            return Task.CompletedTask;
        }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal([(1, 2), (2, 2), (3, 2)], calls);
    }

    [Fact]
    public async Task RetryHold_WhenTheLeaseIsLostDuringTheHold_EndsTheHoldWithoutSettling()
    {
        bool revoked = false;
        var settled = new ConcurrentQueue<string>();
        var transport = new InterceptingMessageTransport(_transport, WithoutRetryDelays() with { LeaseExpiration = false })
        {
            OnRenew = (_, _) => Volatile.Read(ref revoked) ? throw new MessageLeaseLostException("The partition was revoked") : Task.CompletedTask,
            OnSettle = (operation, _) => settled.Enqueue(operation)
        };
        int attempts = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("transient");
        }, new HandlerOptions { RetryPolicy = LongRetries }));
        var status = consumer.Services.GetRequiredService<MessagingStatus>();

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref attempts) == 1 && status.GetProcessors().Single().Active == 1, TimeSpan.FromSeconds(10));
        Volatile.Write(ref revoked, true);
        await WaitForAsync(() => status.GetProcessors().Single().Active == 0, TimeSpan.FromSeconds(10));

        Assert.Empty(settled);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Settle_AfterTheHandlerReturns_GivesTheSlotToTheNextMessageAndReportsTheOutcomeOnceSettled()
    {
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCompleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnSettleAsync = async (operation, message) =>
            {
                if (operation != "complete" || message.MessageId != "first")
                    return;

                completing.TrySetResult();
                await finishCompleting.Task;
            }
        };
        var observer = new RecordingObserver();
        var handled = new ConcurrentQueue<int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, _, _) =>
        {
            handled.Enqueue(message.Id);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 1 }), s => s.AddSingleton<IMessagingObserver>(observer));
        var status = consumer.Services.GetRequiredService<MessagingStatus>();

        await consumer.Client.SendAsync(new RenewedCommand(1), new SendOptions { MessageId = "first" }, TestCancellationToken);
        await completing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await consumer.Client.SendAsync(new RenewedCommand(2), new SendOptions { MessageId = "second" }, TestCancellationToken);

        await WaitForAsync(() => handled.Count == 2 && observer.Outcomes.ContainsKey("second"), TimeSpan.FromSeconds(10));
        Assert.Equal([1, 2], handled);
        Assert.False(observer.Outcomes.ContainsKey("first"), "The outcome was reported before the message was settled.");
        Assert.Equal(1, status.GetProcessors().Single().Active);
        Assert.Equal(1, _transport.GetMessageCount(source));

        finishCompleting.SetResult();
        await WaitForAsync(() => observer.Outcomes.ContainsKey("first"), TimeSpan.FromSeconds(10));
        Assert.Equal("success", observer.Outcomes["first"]);
        Assert.Equal(0, _transport.GetMessageCount(source));
        await WaitForAsync(() => status.GetProcessors().Single().Active == 0, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Settlements without their slots are capped at twice the slots, or at a receive batch when that is more, so a
    /// transport that batches settlements gets a batch's worth together even with one handler.
    /// </summary>
    [Theory]
    [InlineData(2, 1, 4)]
    [InlineData(1, 10, 10)]
    public async Task Settle_WhenTheSettlementsWithoutSlotsAreFull_KeepsFurtherSettlementsInTheirSlots(int maxConcurrency, int receiveBatch, int withoutSlots)
    {
        const int count = 20;
        int expectedHandled = maxConcurrency + withoutSlots;
        int settling = 0;
        var finishCompleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport, _transport.Capabilities with { MaxReceiveBatchSize = receiveBatch })
        {
            OnSettleAsync = async (_, _) =>
            {
                Interlocked.Increment(ref settling);
                await finishCompleting.Task;
            }
        };
        var handled = new ConcurrentDictionary<int, int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, _, _) =>
        {
            handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = maxConcurrency }));
        var status = consumer.Services.GetRequiredService<MessagingStatus>();

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, count).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref settling) == expectedHandled, TimeSpan.FromSeconds(10));

        // Every slot now waits for its own settlement, so no further message may be handled until one finishes.
        await Task.Delay(300, TestCancellationToken);
        Assert.Equal(expectedHandled, handled.Count);
        Assert.Equal(expectedHandled, Volatile.Read(ref settling));
        Assert.Equal(expectedHandled, status.GetProcessors().Single().Active);

        finishCompleting.SetResult();
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal(count, handled.Count);
        Assert.All(handled.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task Settle_InAnOrderedGroup_HandlesTheGroupsNextMessageOnlyOnceTheSettlementFinishes()
    {
        var events = new ConcurrentQueue<string>();
        var finishCompleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnSettleAsync = async (operation, message) =>
            {
                if (operation == "complete" && message.MessageId == "g1")
                    await finishCompleting.Task;

                events.Enqueue($"settled {message.MessageId}");
            }
        };
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>((message, _, _) =>
        {
            events.Enqueue($"handled {message.Id}");
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 4 }));

        for (int id = 1; id <= 3; id++)
            await consumer.Client.SendAsync(new GroupedCommand(id), new SendOptions { GroupKey = "customer-1", MessageId = $"g{id}" }, TestCancellationToken);
        await WaitForAsync(() => events.Contains("handled 1"), TimeSpan.FromSeconds(10));
        for (int id = 10; id <= 11; id++)
            await consumer.Client.SendAsync(new GroupedCommand(id), new SendOptions { MessageId = $"u{id}" }, TestCancellationToken);

        // Other messages are handled and settled meanwhile, but not the group's next one.
        await WaitForAsync(() => events.Contains("settled u10") && events.Contains("settled u11"), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("handled 2", events);

        finishCompleting.SetResult();
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        var order = events.ToList();
        Assert.Equal(["handled 1", "handled 2", "handled 3"], order.Where(e => e is "handled 1" or "handled 2" or "handled 3"));
        Assert.True(order.IndexOf("settled g1") < order.IndexOf("handled 2"), String.Join(", ", order));
        Assert.True(order.IndexOf("settled g2") < order.IndexOf("handled 3"), String.Join(", ", order));
    }

    [Fact]
    public async Task StopAsync_WhileASettlementIsInFlight_WaitsForItToFinish()
    {
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool completed = false;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnSettleAsync = async (operation, _) =>
            {
                completing.TrySetResult();
                await Task.Delay(500, CancellationToken.None);
                Volatile.Write(ref completed, operation == "complete");
            }
        };
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) => Task.CompletedTask, new HandlerOptions { MaxConcurrency = 1 }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);
        await completing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await consumer.DisposeAsync();

        Assert.True(Volatile.Read(ref completed));
        Assert.Equal(0, _transport.GetMessageCount(source));
    }

    [Fact]
    public async Task StopAsync_CalledAgainWhileTheFirstStopDrains_WaitsForTheSameDrain()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int finished = 0;
        await using var consumer = await StartAsync(_transport, m => m.AddConsumer<SlowCommand>(async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            Interlocked.Exchange(ref finished, 1);
        }, new HandlerOptions { MaxConcurrency = 1 }));

        await consumer.Client.SendAsync(new SlowCommand(1), cancellationToken: TestCancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        var processor = GetProcessor(consumer);
        var first = processor.StopAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        var second = processor.StopAsync(TimeSpan.Zero, TestCancellationToken);
        await Task.Delay(100, TestCancellationToken);
        Assert.False(second.IsCompleted, "A second StopAsync returned while the first was still draining.");

        release.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.Equal(1, Volatile.Read(ref finished));
        await first;
    }

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(4, false, 4)]
    [InlineData(4, true, 1)]
    public async Task Receive_WhenIdle_LongPollsAndStillStopsPromptly(int maxConcurrentReceives, bool receiveAhead, int expectedPolls)
    {
        var waits = new ConcurrentQueue<TimeSpan>();
        var capabilities = receiveAhead ? ReceiveAheadCapabilities(maxConcurrentReceives: maxConcurrentReceives) : ConcurrentReceiveCapabilities(maxConcurrentReceives);
        var transport = new InterceptingMessageTransport(_transport, capabilities) { OnReceive = (_, _, wait) => waits.Enqueue(wait) };
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) => Task.CompletedTask, new HandlerOptions { MaxConcurrency = 40 }));
        await WaitForAsync(() => waits.Count >= expectedPolls, TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await consumer.DisposeAsync();

        Assert.Equal(TimeSpan.FromSeconds(20), waits.First());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Stopping took {stopwatch.Elapsed}.");
        Assert.Equal(0, transport.OpenReceivers);
    }

    /// <summary>Reading ahead, one receive more than it takes to fill the slots is in flight, within the transport's limit.</summary>
    [Theory]
    [InlineData(1, 40, false, 1)]
    [InlineData(4, 40, false, 4)]
    [InlineData(8, 15, false, 2)]
    [InlineData(4, 40, true, 4)]
    [InlineData(8, 15, true, 3)]
    public async Task Receive_WithConcurrentReceives_KeepsUpToOneReceivePerBatchOfFreeSlotsInFlight(int maxConcurrentReceives, int maxConcurrency, bool receiveAhead, int expectedReceives)
    {
        const int count = 200;
        var capabilities = receiveAhead ? ReceiveAheadCapabilities(maxConcurrentReceives: maxConcurrentReceives) : ConcurrentReceiveCapabilities(maxConcurrentReceives);
        var transport = new InterceptingMessageTransport(_transport, capabilities) { ReceiveDelay = TimeSpan.FromMilliseconds(50) };
        await using (var producer = await StartAsync(_transport, _ => { }))
            await producer.Client.SendBatchAsync(Enumerable.Range(0, count).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);

        var handled = new ConcurrentDictionary<int, int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, _, _) =>
        {
            handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = maxConcurrency }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(30), TestCancellationToken));
        await WaitForAsync(() => transport.MaxReceivesInFlight >= expectedReceives, TimeSpan.FromSeconds(5));
        Assert.Equal(count, handled.Count);
        Assert.All(handled.Values, n => Assert.Equal(1, n));
        Assert.Equal(expectedReceives, transport.MaxReceivesInFlight);
    }

    /// <summary>
    /// Reading ahead, an idle source keeps one poll in flight, so the failing receives run under a backlog, which keeps
    /// every receive polling.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receive_WhenConcurrentReceivesFailTogether_CountsThemAsOneFailureAndKeepsTheReceiver(bool receiveAhead)
    {
        int backlog = receiveAhead ? 400 : 0;
        int failingReceives = receiveAhead ? 0 : 8, handled = 0;
        var transport = new InterceptingMessageTransport(_transport, receiveAhead ? ReceiveAheadCapabilities() : ConcurrentReceiveCapabilities(4))
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(100),
            OnReceive = (_, _, _) =>
            {
                if (Interlocked.Decrement(ref failingReceives) >= 0)
                    throw new IOException("The broker timed out");
            }
        };
        if (backlog > 0)
            await PrefillAsync(Enumerable.Range(0, backlog).Select(i => new RenewedCommand(i)));

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 40 }));
        if (receiveAhead)
        {
            await WaitForAsync(() => transport.ReceivesInFlight == 4, TimeSpan.FromSeconds(10));
            Volatile.Write(ref failingReceives, 8);
        }

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => Volatile.Read(ref handled) == backlog + 1, TimeSpan.FromSeconds(20));
        Assert.True(Volatile.Read(ref failingReceives) < 0, "Not every failing receive ran.");
        Assert.Equal(1, transport.ReceiversCreated);
        Assert.InRange(Log.LogEntries.Count(e => e.Message.StartsWith("Error receiving from", StringComparison.Ordinal)), 1, 4);
        Assert.DoesNotContain(Log.LogEntries, e => e.Message.Contains("replacing it", StringComparison.Ordinal));
    }

    /// <summary>Reading ahead, an idle source keeps one poll in flight, which sees the fault.</summary>
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public async Task ReplaceReceiver_WithConcurrentReceives_ReplacesTheFaultedReceiverOnceAndKeepsReceiving(bool receiveAhead, int expectedPolls)
    {
        var handled = new ConcurrentQueue<int>();
        var transport = new InterceptingMessageTransport(_transport, receiveAhead ? ReceiveAheadCapabilities() : ConcurrentReceiveCapabilities(4));
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, _, _) =>
        {
            handled.Enqueue(message.Id);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 40 }));
        await WaitForAsync(() => transport.MaxReceivesInFlight == expectedPolls, TimeSpan.FromSeconds(5));

        transport.FaultReceivers();
        await WaitForAsync(() => transport.ReceiversCreated == 2 && transport.OpenReceivers == 1, TimeSpan.FromSeconds(20));
        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => handled.Count == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(2, transport.ReceiversCreated);
        Assert.Equal(1, transport.OpenReceivers);
    }

    [Fact]
    public async Task ReplaceReceiver_WhileAHandlerOfTheFaultedReceiverRuns_CompletesItOnTheFaultedReceiverThenDisposesIt()
    {
        int invocations = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport);
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<SlowCommand>(async (_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            started.TrySetResult();
            await finish.Task;
        }, new HandlerOptions { MaxConcurrency = 4 }));

        await consumer.Client.SendAsync(new SlowCommand(1), cancellationToken: TestCancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        transport.FaultReceivers();

        // The replacement opens at once, and the faulted receiver stays open while its handler runs.
        await WaitForAsync(() => transport.ReceiversCreated == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, transport.OpenReceivers);

        finish.SetResult();
        await WaitForAsync(() => transport.OpenReceivers == 1, TimeSpan.FromSeconds(5));
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task ReplaceReceiver_WhenAHandlerOutlivesTheReplaceTimeout_CancelsItBeforeTheMessageIsDeliveredAgain()
    {
        int invocations = 0, running = 0, maxRunning = 0, cancelled = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport);
        await using var consumer = await StartAsync(transport, m => m
            .Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(1))
            .AddConsumer<SlowCommand>(async (_, context, ct) =>
            {
                Interlocked.Increment(ref invocations);
                InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
                try
                {
                    started.TrySetResult();
                    if (context.Attempt == 1)
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            }));

        await consumer.Client.SendAsync(new SlowCommand(1), cancellationToken: TestCancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        transport.FaultReceivers();

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal(2, Volatile.Read(ref invocations));
        Assert.Equal(1, Volatile.Read(ref cancelled));
        Assert.Equal(1, Volatile.Read(ref maxRunning));
    }

    [Fact]
    public async Task LeaseRenewal_WhenRenewThrowsObjectDisposed_TreatsTheLeaseAsLostAndCancelsTheHandler()
    {
        int renewals = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnRenew = (_, _) =>
            {
                Interlocked.Increment(ref renewals);
                throw new ObjectDisposedException("receiver");
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, context, ct) =>
        {
            if (context.Attempt > 1)
                return;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }, new HandlerOptions { LeaseDuration = ShortLease }));

        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal(1, Volatile.Read(ref renewals));
        Assert.DoesNotContain(Log.LogEntries, e => e.Message.StartsWith("Could not renew the lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeServiceProvider_WithoutStoppingTheHost_StopsEveryProcessor()
    {
        var transport = new InterceptingMessageTransport(_transport);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m.UseTransport(transport).UseServiceName("tests")
            .AddConsumer<RenewedCommand>((_, _, _) => Task.CompletedTask));
        var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
            await hostedService.StartAsync(TestCancellationToken);

        await using var subscription = await provider.GetRequiredService<IMessageClient>()
            .SubscribeAsync<GroupedCommand>((_, _, _) => Task.CompletedTask, cancellationToken: TestCancellationToken);
        await WaitForAsync(() => transport.OpenReceivers == 2, TimeSpan.FromSeconds(5));

        await provider.DisposeAsync();

        Assert.Equal(0, transport.OpenReceivers);
    }

    [Fact]
    public async Task StopAsync_WhenAnotherProcessorFailsToStartDuringShutdown_StillStopsTheStartedOnes()
    {
        int handled = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnCreateReceiver = async (source, ct) =>
            {
                if (!source.Name.Contains(nameof(SlowCommand), StringComparison.Ordinal))
                    return;

                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    throw new IOException("The connection was aborted while connecting");
                }
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton<IStartupGate>(new OpenGate());
        services.AddFoundatioMessaging(m => m.UseTransport(transport).UseServiceName("tests")
            .AddConsumer<RenewedCommand>((_, _, _) => { Interlocked.Increment(ref handled); return Task.CompletedTask; })
            .AddConsumer<SlowCommand>((_, _, _) => Task.CompletedTask));
        await using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        await hostedService.StartAsync(TestCancellationToken);

        await provider.GetRequiredService<IMessageClient>().SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref handled) == 1, TimeSpan.FromSeconds(5));

        await hostedService.StopAsync(TestCancellationToken);

        Assert.Equal(0, transport.OpenReceivers);
        Assert.DoesNotContain(provider.GetRequiredService<MessagingStatus>().GetProcessors(), p => p.State == ProcessingState.Running);
    }

    [Fact]
    public async Task Receive_WhenHandlersAreBusy_AsksForFullBatchesAndNeverRunsMoreThanMaxConcurrency()
    {
        const int maxConcurrency = 4, count = 60;
        var asks = new ConcurrentQueue<int>();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: maxConcurrency))
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(50),
            OnReceive = (_, maxMessages, _) => asks.Enqueue(maxMessages)
        };
        await PrefillAsync(Enumerable.Range(0, count).Select(i => new RenewedCommand(i)));

        int running = 0, maxRunning = 0;
        var handled = new ConcurrentDictionary<int, int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
            await Task.Delay(30, CancellationToken.None);
            Interlocked.Decrement(ref running);
            handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        }, new HandlerOptions { MaxConcurrency = maxConcurrency }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(30), TestCancellationToken));
        Assert.Equal(count, handled.Count);
        Assert.All(handled.Values, n => Assert.Equal(1, n));
        Assert.All(asks, ask => Assert.Equal(maxConcurrency, ask));
        Assert.Equal(maxConcurrency, Volatile.Read(ref maxRunning));

        // Two receives in flight, one more than fills the slots, read at most two batches and one more ahead of them.
        Assert.InRange(transport.MaxUnsettled, maxConcurrency + 1, maxConcurrency + 3 * maxConcurrency);
    }

    [Fact]
    public async Task Receive_WithFewerHandlersThanABatchAndNoOrderedGroups_AsksForFullBatches()
    {
        const int count = 300;
        var asks = new ConcurrentQueue<int>();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities() with { OrderedGroups = false })
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(5),
            OnReceive = (_, maxMessages, _) => asks.Enqueue(maxMessages)
        };
        await PrefillAsync(Enumerable.Range(0, count).Select(i => new RenewedCommand(i)));

        int running = 0, maxRunning = 0;
        var handled = new ConcurrentDictionary<int, int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
            await Task.Yield();
            Interlocked.Decrement(ref running);
            handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        }, new HandlerOptions { MaxConcurrency = 1 }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(30), TestCancellationToken));
        Assert.Equal(count, handled.Count);
        Assert.All(handled.Values, n => Assert.Equal(1, n));
        Assert.Equal(1, Volatile.Read(ref maxRunning));

        // Every message is its own sequence, so one handler reads ahead in the transport's full batches rather than one
        // message per receive.
        Assert.Contains(10, asks);
        Assert.True(transport.ReceiveCalls < count / 4, $"{transport.ReceiveCalls} receives for {count} messages.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Receive_WithOneOutstandingMessagePerGroup_ReadsAheadInFullBatchesAndKeepsGroupOrder(int maxConcurrency)
    {
        const int groups = 40, perGroup = 10;
        var capabilities = ReceiveAheadCapabilities() with { OneOutstandingPerGroup = true };
        await using var inner = new InMemoryMessageTransport(capabilities);
        var asks = new ConcurrentQueue<int>();
        var transport = new InterceptingMessageTransport(inner)
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(2),
            OnReceive = (_, maxMessages, _) => asks.Enqueue(maxMessages)
        };

        await using (var producer = await StartAsync(inner, _ => { }))
        {
            for (int i = 0; i < perGroup; i++)
            {
                foreach (int group in Enumerable.Range(0, groups))
                    await producer.Client.SendAsync(new GroupedCommand(group * 100 + i), new SendOptions { GroupKey = $"g{group}" }, TestCancellationToken);
            }
        }

        int running = 0, maxRunning = 0;
        var handled = new ConcurrentDictionary<int, ConcurrentQueue<int>>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>(async (message, _, _) =>
        {
            InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
            await Task.Yield();
            Interlocked.Decrement(ref running);
            handled.GetOrAdd(message.Id / 100, _ => new()).Enqueue(message.Id % 100);
        }, new HandlerOptions { MaxConcurrency = maxConcurrency }));

        Assert.True(await inner.WaitUntilIdleAsync(TimeSpan.FromSeconds(30), TestCancellationToken));
        Assert.Equal(groups, handled.Count);
        Assert.All(handled.Values, order => Assert.Equal(Enumerable.Range(0, perGroup), order));
        Assert.True(Volatile.Read(ref maxRunning) <= maxConcurrency);

        // No receive can return two messages of a group, so batches need not fit in the handler slots.
        Assert.Contains(10, asks);
    }

    [Fact]
    public async Task Receive_WhenATransportBreaksOneOutstandingPerGroup_ReleasesWhatDoesNotFitAndKeepsGroupOrder()
    {
        // The in-memory transport returns a group's next messages in the same batch, which the declared capability rules out.
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities() with { OneOutstandingPerGroup = true })
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(2)
        };

        // Ungrouped messages first, so the processor has measured what it needs to read ahead when it reaches the group.
        await PrefillAsync(Enumerable.Range(1, 30).Select(i => new GroupedCommand(-i)));
        await PrefillAsync(Enumerable.Range(1, 5).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "g" });

        var handled = new ConcurrentQueue<int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>((message, _, _) =>
        {
            if (message.Id > 0)
                handled.Enqueue(message.Id);

            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 1 }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(30), TestCancellationToken));
        Assert.Equal([1, 2, 3, 4, 5], handled);
        Assert.Contains(Log.LogEntries, e => e.LogLevel == LogLevel.Error && e.Message.Contains("OneOutstandingPerGroup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receive_WhenAReceiveReturnsLessThanItAsked_ParksTheOtherReceives()
    {
        const int count = 400;
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities())
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(20),
            MaxReceiveWait = TimeSpan.FromMilliseconds(20)
        };
        await PrefillAsync(Enumerable.Range(0, count).Select(i => new RenewedCommand(i)));
        int handled = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 40 }));

        await WaitForAsync(() => Volatile.Read(ref handled) == count, TimeSpan.FromSeconds(20));
        Assert.Equal(4, transport.MaxReceivesInFlight);

        // Drained, every receive comes back short, so once those in flight have returned only the first polls.
        int calls = transport.ReceiveCalls;
        await WaitForAsync(() => transport.ReceiveCalls >= calls + 8, TimeSpan.FromSeconds(10));
        transport.ResetMaxReceivesInFlight();
        calls = transport.ReceiveCalls;
        await WaitForAsync(() => transport.ReceiveCalls >= calls + 5, TimeSpan.FromSeconds(10));
        Assert.Equal(1, transport.MaxReceivesInFlight);

        // A receive that returns a full batch lets one more poll.
        await consumer.Client.SendBatchAsync(Enumerable.Range(count, count).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref handled) == 2 * count, TimeSpan.FromSeconds(20));
        Assert.True(transport.MaxReceivesInFlight > 1, $"At most {transport.MaxReceivesInFlight} receive was in flight.");
    }

    [Fact]
    public async Task Receive_WhenIdle_KeepsTheMinimumOfLongPollsInFlight()
    {
        var waits = new ConcurrentQueue<TimeSpan>();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities()) { OnReceive = (_, _, wait) => waits.Enqueue(wait) };
        int handled = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 40 }));
        await WaitForAsync(() => transport.ReceivesInFlight == 1, TimeSpan.FromSeconds(5));

        // Each message wakes the one long poll, which returns less than it asked for, so no other starts.
        for (int i = 1; i <= 5; i++)
        {
            int expected = i;
            await consumer.Client.SendAsync(new RenewedCommand(i), cancellationToken: TestCancellationToken);
            await WaitForAsync(() => Volatile.Read(ref handled) == expected, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, transport.MaxReceivesInFlight);
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(20), wait));
    }

    [Fact]
    public async Task Receive_WithReceiveAheadOff_AsksOnlyForFreeSlots()
    {
        const int maxConcurrency = 4;
        var asks = new ConcurrentQueue<int>();
        var transport = new InterceptingMessageTransport(_transport, ConcurrentReceiveCapabilities(4)) { OnReceive = (_, maxMessages, _) => asks.Enqueue(maxMessages) };
        await PrefillAsync(Enumerable.Range(0, 20).Select(i => new RenewedCommand(i)));
        using var release = new SemaphoreSlim(0);
        int started = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (_, _, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.WaitAsync(ct);
        }, new HandlerOptions { MaxConcurrency = maxConcurrency }));
        await WaitForAsync(() => Volatile.Read(ref started) == maxConcurrency, TimeSpan.FromSeconds(5));

        Assert.Equal([maxConcurrency], asks);
        Assert.Equal(0, transport.ReceivesInFlight);
        Assert.Equal(maxConcurrency, transport.MaxUnsettled);

        release.Release();
        await WaitForAsync(() => Volatile.Read(ref started) == maxConcurrency + 1, TimeSpan.FromSeconds(5));
        Assert.Equal([maxConcurrency, 1], asks);
        Assert.Equal(maxConcurrency, transport.MaxUnsettled);
        release.Release(20);
    }

    [Fact]
    public async Task StopAsync_WithBufferedMessages_HandlesThemWithinTheDrain()
    {
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        var settled = new ConcurrentQueue<string>();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2)) { OnSettle = (operation, _) => settled.Enqueue(operation) };
        await PrefillAsync(WarmUp(2));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new ConcurrentDictionary<int, int>();
        int running = 0;
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            if (message.Id >= 0)
            {
                Interlocked.Increment(ref running);
                await gate.Task;
            }

            handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        }, new HandlerOptions { MaxConcurrency = 2 }));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => handled.Count == 2 && processor.InFlight == 0, TimeSpan.FromSeconds(5));

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 3).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref running) == 2 && processor.GetStatus().Buffered == 1, TimeSpan.FromSeconds(5));

        var stopping = consumer.DisposeAsync().AsTask();
        await WaitForAsync(() => processor.GetStatus().State == ProcessingState.Stopped, TimeSpan.FromSeconds(5));
        Assert.Equal(1, processor.GetStatus().Buffered);
        gate.SetResult();
        await stopping;

        Assert.Equal(5, handled.Count);
        Assert.All(handled.Values, n => Assert.Equal(1, n));
        Assert.All(settled, operation => Assert.Equal("complete", operation));
        Assert.Equal(0, _transport.GetMessageCount(source));
    }

    [Fact]
    public async Task StopAsync_WhenTheDrainTimesOut_ReleasesBufferedMessagesWithoutRunningTheirHandlers()
    {
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        var settled = new ConcurrentQueue<string>();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2)) { OnSettle = (operation, _) => settled.Enqueue(operation) };
        await PrefillAsync(WarmUp(2));
        var started = new ConcurrentQueue<int>();
        var consumer = await StartAsync(transport, m => m
            .Configure(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(200))
            .AddConsumer<RenewedCommand>(async (message, _, ct) =>
            {
                started.Enqueue(message.Id);
                if (message.Id >= 0)
                    await Task.Delay(Timeout.Infinite, ct);
            }, new HandlerOptions { MaxConcurrency = 2 }));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => started.Count == 2 && processor.InFlight == 0, TimeSpan.FromSeconds(5));

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 3).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => started.Count == 4 && processor.GetStatus().Buffered == 1, TimeSpan.FromSeconds(5));
        int buffered = Enumerable.Range(0, 3).Single(i => !started.Contains(i));

        await consumer.DisposeAsync();

        Assert.DoesNotContain(buffered, started);
        Assert.Equal(["abandon", "abandon", "abandon"], settled.Where(s => s != "complete"));
        Assert.Equal(3, _transport.GetMessageCount(source));
    }

    [Fact]
    public async Task ReplaceReceiver_WithBufferedMessages_ReleasesThemWithoutRunningTheirHandlers()
    {
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2));
        await PrefillAsync(WarmUp(2));
        var calls = new ConcurrentQueue<(int Id, int Attempt)>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, context, _) =>
        {
            calls.Enqueue((message.Id, context.Attempt));
            if (message.Id >= 0 && context.Attempt == 1)
                await gate.Task;
        }, new HandlerOptions { MaxConcurrency = 2 }));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => calls.Count == 2 && processor.InFlight == 0, TimeSpan.FromSeconds(5));

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 3).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => calls.Count == 4 && processor.GetStatus().Buffered == 1 && transport.ReceivesInFlight > 0, TimeSpan.FromSeconds(5));
        int buffered = Enumerable.Range(0, 3).Single(i => !calls.Any(c => c.Id == i));

        // A receive in flight sees the fault, and the receiver is replaced.
        transport.FaultReceivers();
        await WaitForAsync(() => processor.GetStatus().Buffered == 0, TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(calls, c => c.Id == buffered);
        gate.SetResult();

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));
        Assert.Equal([(buffered, 2)], calls.Where(c => c.Id == buffered));
        Assert.Equal(5, calls.Count);
        Assert.Equal(2, transport.ReceiversCreated);
    }

    [Fact]
    public async Task LeaseRenewal_WhileBuffered_RenewsFromReceipt()
    {
        var time = new FakeTimeProvider();
        var renewed = new ConcurrentQueue<string>();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 1, maxConcurrentReceives: 1))
        {
            OnRenew = (message, _) =>
            {
                renewed.Enqueue(message.MessageId);
                return Task.CompletedTask;
            }
        };
        await PrefillAsync(WarmUp(1));
        var started = new ConcurrentQueue<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            started.Enqueue(message.Id);
            if (message.Id >= 0)
                await gate.Task;
        }, new HandlerOptions { MaxConcurrency = 1, LeaseDuration = TimeSpan.FromSeconds(30) }), s => s.AddSingleton<TimeProvider>(time));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => started.Count == 1 && processor.InFlight == 0, TimeSpan.FromSeconds(5));

        await consumer.Client.SendAsync(new RenewedCommand(0), new SendOptions { MessageId = "running" }, TestCancellationToken);
        await consumer.Client.SendAsync(new RenewedCommand(1), new SendOptions { MessageId = "buffered" }, TestCancellationToken);
        await WaitForAsync(() => started.Count == 2 && processor.GetStatus().Buffered == 1, TimeSpan.FromSeconds(5));
        Assert.Empty(renewed);

        // A third of the lease after it was received, the buffered message is renewed like the one being handled.
        time.Advance(TimeSpan.FromSeconds(11));
        await WaitForAsync(() => renewed.Contains("buffered") && renewed.Contains("running"), TimeSpan.FromSeconds(5));
        Assert.Equal([-1, 0], started);

        gate.SetResult();
        await WaitForAsync(() => started.Count == 3 && processor.InFlight == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Receive_WhenALongPollOutlastsALeaseCountedFromTheRequest_KeepsReadingAhead()
    {
        var time = new FakeTimeProvider();
        var lockDuration = TimeSpan.FromSeconds(30);
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2, maxConcurrentReceives: 1))
        {
            BrokerLeaseDuration = lockDuration,
            BrokerLeaseFromRequest = true,
            TimeProvider = time
        };
        await PrefillAsync(WarmUp(2));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int handled = 0, running = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            if (message.Id >= 0)
            {
                Interlocked.Increment(ref running);
                await gate.Task;
            }

            Interlocked.Increment(ref handled);
        }, new HandlerOptions { MaxConcurrency = 2 }), s => s.AddSingleton<TimeProvider>(time));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => Volatile.Read(ref handled) == 2 && processor.InFlight == 0 && transport.ReceivesInFlight == 1, TimeSpan.FromSeconds(5));

        // The idle long poll outlasts the lock before a message arrives, so the message reports a lease that is already
        // over. It counts as lost, so it is released without running its handler.
        time.Advance(lockDuration + TimeSpan.FromSeconds(1));
        await consumer.Client.SendAsync(new RenewedCommand(-3), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => transport.ReceivesInFlight == 1 && processor.InFlight == 0 && Log.LogEntries.Any(e => e.Message.StartsWith("Lost the lease", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref handled));

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 4).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref running) == 2 && processor.GetStatus().Buffered > 0, TimeSpan.FromSeconds(5));

        gate.SetResult();
        await WaitForAsync(() => Volatile.Read(ref handled) == 6 && processor.InFlight == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Receive_WhenABurstFillsALongPollOfADrainedSource_DoesNotCountTheWaitAsTheReceiveTime()
    {
        // A clock that moves only when the test advances it keeps the depth at a batch, two.
        var time = new FakeTimeProvider();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2, maxConcurrentReceives: 1));
        await PrefillAsync(WarmUp(2));
        using var gate = new SemaphoreSlim(0);
        int handled = 0, running = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            if (message.Id >= 100)
            {
                Interlocked.Increment(ref running);
                await gate.WaitAsync(TestCancellationToken);
            }

            Interlocked.Increment(ref handled);
        }, new HandlerOptions { MaxConcurrency = 2 }), s => s.AddSingleton<TimeProvider>(time));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => Volatile.Read(ref handled) == 2 && processor.InFlight == 0 && transport.ReceivesInFlight == 1, TimeSpan.FromSeconds(5));

        // Five seconds into the long poll, a burst due at once fills the batch it asked for.
        time.Advance(TimeSpan.FromSeconds(5));
        await PrefillAsync(Enumerable.Range(0, 2).Select(i => new RenewedCommand(i)), new SendOptions { DeliverAt = DateTimeOffset.UtcNow.AddMilliseconds(100) });
        await WaitForAsync(() => Volatile.Read(ref handled) == 4 && processor.InFlight == 0, TimeSpan.FromSeconds(5));

        // Had the wait counted as a receive, the depth would cover the receive as well as a batch, four.
        await consumer.Client.SendBatchAsync(Enumerable.Range(100, 8).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref running) == 2 && processor.Buffered > 0 && transport.ReceivesInFlight == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(2, processor.Buffered);

        gate.Release(8);
        await WaitForAsync(() => Volatile.Read(ref handled) == 12 && processor.InFlight == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Receive_WithLargeBodies_BuffersNoMoreThanFitWithinTheByteCap()
    {
        // A clock that does not move keeps the depth at a batch, two, so only the bodies limit it.
        var time = new FakeTimeProvider();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2, maxConcurrentReceives: 2));
        string data = new('x', LargeBodyLength);
        await PrefillAsync(Enumerable.Range(1, 2).Select(i => new LargeCommand(-i, data)));
        using var gate = new SemaphoreSlim(0);
        int handled = 0, running = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<LargeCommand>(async (message, _, _) =>
        {
            if (message.Id >= 0)
            {
                Interlocked.Increment(ref running);
                await gate.WaitAsync(TestCancellationToken);
            }

            Interlocked.Increment(ref handled);
        }, new HandlerOptions { MaxConcurrency = 2 }), s => s.AddSingleton<TimeProvider>(time));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => Volatile.Read(ref handled) == 2 && processor.InFlight == 0 && transport.ReceivesInFlight == 2, TimeSpan.FromSeconds(10));

        // Two bodies would exceed the cap, so the receives ask for the two free slots and one message more.
        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 6).Select(i => new LargeCommand(i, data)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref running) == 2 && processor.Buffered > 0 && transport.ReceivesInFlight == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(1, processor.Buffered);
        Assert.Equal(3, transport.MaxUnsettled);

        gate.Release(6);
        await WaitForAsync(() => Volatile.Read(ref handled) == 8 && processor.InFlight == 0, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Receive_WhenBufferedBodiesFallBackWithinTheByteCap_LetsTheParkedReceivePollAgain()
    {
        // One-message batches, so two receives fill the slots and a third reads ahead.
        var time = new FakeTimeProvider();
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 1, maxConcurrentReceives: 3));
        using var gate = new SemaphoreSlim(0);
        int handled = 0, running = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<LargeCommand>(async (message, _, _) =>
        {
            if (message.Id >= 0 && message.Data.Length == 0)
            {
                Interlocked.Increment(ref running);
                await gate.WaitAsync(TestCancellationToken);
            }

            Interlocked.Increment(ref handled);
        }, new HandlerOptions { MaxConcurrency = 2 }), s => s.AddSingleton<TimeProvider>(time));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => transport.ReceivesInFlight == 1, TimeSpan.FromSeconds(5));

        // A receive that waited half a second for its message makes the depth a message per receive and one more, four,
        // so with the slots taken, all three receives read ahead.
        time.Advance(TimeSpan.FromMilliseconds(500));
        await consumer.Client.SendBatchAsync(Enumerable.Range(1, 2).Select(i => new LargeCommand(-i, "")), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref handled) == 2 && processor.InFlight == 0 && transport.ReceivesInFlight == 3, TimeSpan.FromSeconds(5));
        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 2).Select(i => new LargeCommand(i, "")), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref running) == 2 && transport.ReceivesInFlight == 3, TimeSpan.FromSeconds(5));

        // They asked while the bodies were small, so large ones overshoot the cap, and the depth falls to zero.
        string data = new('x', LargeBodyLength);
        await consumer.Client.SendBatchAsync(Enumerable.Range(10, 3).Select(i => new LargeCommand(i, data)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => processor.Buffered == 3 && transport.ReceivesInFlight == 0, TimeSpan.FromSeconds(10));

        // Once they drain, one large body fits again, and every receive polls: two for the free slots, and one more.
        gate.Release(2);
        await WaitForAsync(() => Volatile.Read(ref handled) == 7 && processor.InFlight == 0 && transport.ReceivesInFlight == 3, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OrderedGroupBatch_WhenBuffered_HandlesEachGroupInOrderOneAtATime()
    {
        const int maxConcurrency = 4;
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: maxConcurrency, maxConcurrentReceives: 1));
        await PrefillAsync(Enumerable.Range(1, maxConcurrency).Select(i => new GroupedCommand(-i)));
        await PrefillAsync(Enumerable.Range(100, maxConcurrency).Select(i => new GroupedCommand(i)));
        await PrefillAsync(Enumerable.Range(1, 3).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "a" });
        await PrefillAsync(Enumerable.Range(11, 3).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "b" });

        using var blockers = new SemaphoreSlim(0);
        var events = new ConcurrentQueue<string>();
        int blocked = 0, overlapped = 0;
        var running = new ConcurrentDictionary<string, int>();
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>(async (message, context, _) =>
        {
            if (message.Id >= 100)
            {
                Interlocked.Increment(ref blocked);
                await blockers.WaitAsync(TestCancellationToken);
                return;
            }

            if (context.GroupKey is not { } group)
                return;

            if (running.AddOrUpdate(group, 1, (_, n) => n + 1) > 1)
                Interlocked.Increment(ref overlapped);

            events.Enqueue($"{group}{message.Id}");
            await Task.Delay(10, CancellationToken.None);
            running.AddOrUpdate(group, 0, (_, n) => n - 1);
        }, new HandlerOptions { MaxConcurrency = maxConcurrency }));
        var processor = GetProcessor(consumer);

        // The blockers hold every slot; the first group's sequence and the second group's first message wait, in that order.
        await WaitForAsync(() => Volatile.Read(ref blocked) == maxConcurrency && processor.GetStatus().Buffered == 4, TimeSpan.FromSeconds(10));
        blockers.Release();
        await WaitForAsync(() => processor.GetStatus().Buffered == 3, TimeSpan.FromSeconds(5));
        blockers.Release();
        await WaitForAsync(() => processor.GetStatus().Buffered == 2, TimeSpan.FromSeconds(5));

        // b11 would fit in a free slot, but it waits behind the sequence received before it.
        Assert.Empty(events);
        blockers.Release();
        await WaitForAsync(() => events.Count == 6, TimeSpan.FromSeconds(10));
        blockers.Release();

        var order = events.ToList();
        Assert.Equal(["a1", "a2", "a3"], order.Where(e => e[0] == 'a'));
        Assert.Equal(["b11", "b12", "b13"], order.Where(e => e[0] == 'b'));
        Assert.True(order.IndexOf("a1") < order.IndexOf("b11"), String.Join(", ", order));
        Assert.Equal(0, Volatile.Read(ref overlapped));
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
    }

    [Fact]
    public async Task RetryHold_WithBufferedMessages_GivesItsSlotToTheNextBufferedMessage()
    {
        var source = MessageSource.Queue(QueueName<RenewedCommand>());
        var settled = new ConcurrentQueue<(string Operation, string MessageId)>();
        var capabilities = WithoutRetryDelays() with { MaxReceiveBatchSize = 1, ReceiveAhead = true };
        var transport = new InterceptingMessageTransport(_transport, capabilities) { OnSettle = (operation, message) => settled.Enqueue((operation, message.MessageId)) };
        await PrefillAsync(WarmUp(1));
        var failing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new ConcurrentQueue<(int Id, int Attempt)>();
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, context, _) =>
        {
            handled.Enqueue((message.Id, context.Attempt));
            if (message.Id == 0)
            {
                await failing.Task;
                throw new InvalidOperationException("transient");
            }
        }, new HandlerOptions { MaxConcurrency = 1, RetryPolicy = LongRetries }));
        var processor = GetProcessor(consumer);
        await WaitForAsync(() => handled.Count == 1 && processor.InFlight == 0, TimeSpan.FromSeconds(5));

        await consumer.Client.SendAsync(new RenewedCommand(0), new SendOptions { MessageId = "held" }, TestCancellationToken);
        await consumer.Client.SendAsync(new RenewedCommand(1), new SendOptions { MessageId = "next" }, TestCancellationToken);
        await WaitForAsync(() => handled.Count == 2 && processor.GetStatus().Buffered == 1, TimeSpan.FromSeconds(5));

        failing.SetResult();
        await WaitForAsync(() => handled.Contains((1, 1)) && processor.InFlight == 1, TimeSpan.FromSeconds(5));
        var status = processor.GetStatus();
        Assert.Equal((1, 0), (status.Active, status.Buffered));
        Assert.DoesNotContain(settled, s => s.MessageId == "held");
        Assert.Contains(("complete", "next"), settled);

        await consumer.DisposeAsync();
        Assert.Contains(("abandon", "held"), settled);
        Assert.Equal(1, _transport.GetMessageCount(source));
    }

    [Fact]
    public async Task GetStatus_WithBufferedMessages_ReportsThemAsBufferedNotActive()
    {
        var transport = new InterceptingMessageTransport(_transport, ReceiveAheadCapabilities(maxReceiveBatchSize: 2));
        await PrefillAsync(WarmUp(2));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int warmedUp = 0, running = 0;
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>(async (message, _, _) =>
        {
            if (message.Id < 0)
            {
                Interlocked.Increment(ref warmedUp);
                return;
            }

            Interlocked.Increment(ref running);
            await gate.Task;
        }, new HandlerOptions { MaxConcurrency = 2 }));
        var status = consumer.Services.GetRequiredService<MessagingStatus>();
        await WaitForAsync(() => Volatile.Read(ref warmedUp) == 2 && status.GetProcessors().Single().Active == 0, TimeSpan.FromSeconds(5));

        await consumer.Client.SendBatchAsync(Enumerable.Range(0, 3).Select(i => new RenewedCommand(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref running) == 2 && status.GetProcessors().Single().Buffered == 1, TimeSpan.FromSeconds(5));

        var processing = status.GetProcessors().Single();
        Assert.Equal((2, 1), (processing.Active, processing.Buffered));
        gate.SetResult();
        await WaitForAsync(() => status.GetProcessors().Single() is { Active: 0, Buffered: 0 }, TimeSpan.FromSeconds(5));
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task<MessagingTestHost> StartAsync(IMessageTransport transport, Action<FoundatioMessagingBuilder> configure, Action<IServiceCollection>? configureServices = null)
        => MessagingTestHost.StartAsync(transport, Log, "tests", configure, configureServices);

    private static string QueueName<T>() => typeof(T).FullName!.Replace('+', '.');

    private static TransportCapabilities ConcurrentReceiveCapabilities(int maxConcurrentReceives)
        => new InMemoryMessageTransport().Capabilities with { MaxReceiveBatchSize = 10, MaxConcurrentReceives = maxConcurrentReceives };

    private static TransportCapabilities ReceiveAheadCapabilities(int maxReceiveBatchSize = 10, int maxConcurrentReceives = 4)
        => ConcurrentReceiveCapabilities(maxConcurrentReceives) with { MaxReceiveBatchSize = maxReceiveBatchSize, ReceiveAhead = true };

    /// <summary>
    /// Messages the tests' handlers return from at once. Sent before a consumer starts, they fill its first receive and
    /// finish at once, so the processor has measured both times it needs to read ahead.
    /// </summary>
    private static IEnumerable<RenewedCommand> WarmUp(int count) => Enumerable.Range(1, count).Select(i => new RenewedCommand(-i));

    private async Task PrefillAsync<T>(IEnumerable<T> messages, SendOptions? options = null) where T : class
    {
        await using var producer = await StartAsync(_transport, _ => { });
        await producer.Client.SendBatchAsync(messages, options, TestCancellationToken);
    }

    private static MessageProcessor GetProcessor(MessagingTestHost host) => host.Services.GetRequiredService<MessagingRuntime>().GetProcessors().Single();

    private static TransportCapabilities WithoutRetryDelays() => new InMemoryMessageTransport().Capabilities with { MaxRetryDelay = TimeSpan.Zero };

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(20, TestCancellationToken);
        }
    }

    public record GroupedCommand(int Id);
    public record LargeCommand(int Id, string Data);
    public record RenewedCommand(int Id);
    public record SlowCommand(int Id);
    public record PoisonCommand(int Id);

    /// <summary>The system clock, moved forward by <see cref="Jump"/>; timers run on real time, so they fire late after a jump.</summary>
    private sealed class JumpingTimeProvider : TimeProvider
    {
        private long _skewTicks;

        public void Jump(TimeSpan by) => Interlocked.Add(ref _skewTicks, by.Ticks);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _skewTicks));
    }

    private sealed class OpenGate : IStartupGate
    {
        public Task<bool> WaitAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>Throws from its first delivery before settling it, as a throwing logging scope would, and completes the rest.</summary>
    private sealed class ThrowOnceDeliveryHandler : IMessageDeliveryHandler
    {
        public int Calls;

        public ConcurrentQueue<string> Released { get; } = new();

        public async Task<bool> HandleAsync(MessageDelivery delivery, CancellationToken abortToken)
        {
            if (Interlocked.Increment(ref Calls) == 1)
                throw new InvalidOperationException("Handling failed before settling");

            return await delivery.SettleAsync(static (receiver, message, ct) => receiver.CompleteAsync(message, ct), "complete", NullLogger.Instance) is null;
        }

        public void OnReleased(MessageDelivery delivery, string outcome) => Released.Enqueue(outcome);
    }

    /// <summary>Records the outcome reported for each message id.</summary>
    private sealed class RecordingObserver : IMessagingObserver
    {
        public ConcurrentDictionary<string, string> Outcomes { get; } = new();

        public void OnSent(MessageDestination destination, object message, OutgoingMessage outgoing)
        {
        }

        public void OnHandled(MessageSource source, IncomingMessage message, object? payload, string outcome, string? reason, Exception? error, MessageContext? context = null)
            => Outcomes[message.MessageId] = outcome;
    }
}
