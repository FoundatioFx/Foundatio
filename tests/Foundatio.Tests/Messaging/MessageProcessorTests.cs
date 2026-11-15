using System;
using System.Collections.Concurrent;
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
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Leases, settlement and lifecycle of the processors that run message handlers.</summary>
public class MessageProcessorTests : TestWithLoggingBase
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(1);
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
        var calls = new ConcurrentQueue<(int Id, int Attempt)>();
        var consumer = await StartAsync(transport, m => m.AddConsumer<GroupedCommand>((message, context, _) =>
        {
            calls.Enqueue((message.Id, context.Attempt));
            return message.Id == 1 && context.Attempt == 1 ? throw new InvalidOperationException("transient") : Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 3, RetryPolicy = LongRetries }));

        await consumer.Client.SendBatchAsync(Enumerable.Range(1, 3).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "customer-1" }, TestCancellationToken);
        await consumer.Client.SendBatchAsync(Enumerable.Range(10, 3).Select(i => new GroupedCommand(i)), cancellationToken: TestCancellationToken);
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

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Receive_WhenIdle_LongPollsAndStillStopsPromptly(int maxConcurrentReceives)
    {
        var waits = new ConcurrentQueue<TimeSpan>();
        var transport = new InterceptingMessageTransport(_transport, ConcurrentReceiveCapabilities(maxConcurrentReceives)) { OnReceive = (_, _, wait) => waits.Enqueue(wait) };
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) => Task.CompletedTask, new HandlerOptions { MaxConcurrency = 40 }));
        await WaitForAsync(() => waits.Count >= maxConcurrentReceives, TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await consumer.DisposeAsync();

        Assert.Equal(TimeSpan.FromSeconds(20), waits.First());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Stopping took {stopwatch.Elapsed}.");
        Assert.Equal(0, transport.OpenReceivers);
    }

    [Theory]
    [InlineData(1, 40, 1)]
    [InlineData(4, 40, 4)]
    [InlineData(8, 15, 2)]
    public async Task Receive_WithConcurrentReceives_KeepsUpToOneReceivePerBatchOfFreeSlotsInFlight(int maxConcurrentReceives, int maxConcurrency, int expectedReceives)
    {
        const int count = 200;
        var transport = new InterceptingMessageTransport(_transport, ConcurrentReceiveCapabilities(maxConcurrentReceives)) { ReceiveDelay = TimeSpan.FromMilliseconds(50) };
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

    [Fact]
    public async Task Receive_WhenConcurrentReceivesFailTogether_CountsThemAsOneFailureAndKeepsTheReceiver()
    {
        int failingReceives = 8, handled = 0;
        var transport = new InterceptingMessageTransport(_transport, ConcurrentReceiveCapabilities(4))
        {
            ReceiveDelay = TimeSpan.FromMilliseconds(100),
            OnReceive = (_, _, _) =>
            {
                if (Interlocked.Decrement(ref failingReceives) >= 0)
                    throw new IOException("The broker timed out");
            }
        };

        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 40 }));
        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => Volatile.Read(ref handled) == 1, TimeSpan.FromSeconds(20));
        Assert.True(Volatile.Read(ref failingReceives) < 0, "Not every failing receive ran.");
        Assert.Equal(1, transport.ReceiversCreated);
        Assert.InRange(Log.LogEntries.Count(e => e.Message.StartsWith("Error receiving from", StringComparison.Ordinal)), 1, 4);
        Assert.DoesNotContain(Log.LogEntries, e => e.Message.Contains("replacing it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceReceiver_WithConcurrentReceives_ReplacesTheFaultedReceiverOnceAndKeepsReceiving()
    {
        var handled = new ConcurrentQueue<int>();
        var transport = new InterceptingMessageTransport(_transport, ConcurrentReceiveCapabilities(4));
        await using var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((message, _, _) =>
        {
            handled.Enqueue(message.Id);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 40 }));
        await WaitForAsync(() => transport.MaxReceivesInFlight == 4, TimeSpan.FromSeconds(5));

        transport.FaultReceivers();
        await WaitForAsync(() => transport.ReceiversCreated == 2 && transport.OpenReceivers == 1, TimeSpan.FromSeconds(20));
        await consumer.Client.SendAsync(new RenewedCommand(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => handled.Count == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(2, transport.ReceiversCreated);
        Assert.Equal(1, transport.OpenReceivers);
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

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task<MessagingTestHost> StartAsync(IMessageTransport transport, Action<FoundatioMessagingBuilder> configure)
        => MessagingTestHost.StartAsync(transport, Log, "tests", configure);

    private static string QueueName<T>() => typeof(T).FullName!.Replace('+', '.');

    private static TransportCapabilities ConcurrentReceiveCapabilities(int maxConcurrentReceives)
        => new InMemoryMessageTransport().Capabilities with { MaxReceiveBatchSize = 10, MaxConcurrentReceives = maxConcurrentReceives };

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
    public record RenewedCommand(int Id);
    public record SlowCommand(int Id);
    public record PoisonCommand(int Id);

    private sealed class OpenGate : IStartupGate
    {
        public Task<bool> WaitAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
