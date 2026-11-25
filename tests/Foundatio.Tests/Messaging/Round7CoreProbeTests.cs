using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Round 7 review probes for the message runtime. Each fails while the defect it names is present.</summary>
[Trait("Round7", "core")]
public class Round7CoreProbeTests : TestWithLoggingBase
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(1);
    private readonly InMemoryMessageTransport _transport = new();

    public Round7CoreProbeTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task LeaseRenewal_WhenRenewalsKeepFailingPastTheLease_DoesNotRunTheHandlerAlongsideItsRedelivery()
    {
        int running = 0, maxRunning = 0, invocations = 0, cancelled = 0;
        var transport = new InterceptingMessageTransport(_transport)
        {
            OnRenew = (_, _) => throw new IOException("The broker cannot be reached")
        };

        await using var consumer = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m.AddConsumer<ProbeCommand>(async (_, context, ct) =>
        {
            Interlocked.Increment(ref invocations);
            InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
            try
            {
                if (context.Attempt == 1)
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
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
        }, new HandlerOptions { LeaseDuration = ShortLease, MaxConcurrency = 4 }));

        await consumer.Client.SendAsync(new ProbeCommand(1), cancellationToken: TestCancellationToken);
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(20), TestCancellationToken));

        // The lease ran out about 1 s into the 4 s handler, so the message was delivered again while it still ran.
        // The renewer only retries a failed renewal; nothing cancels the handler once the lease it could not renew has ended.
        Assert.True(Volatile.Read(ref invocations) >= 2, "The message was never delivered again, so the probe did not exercise an expired lease.");
        Assert.Equal(1, Volatile.Read(ref maxRunning));
    }

    [Fact]
    public async Task HostStop_WhileMessageHandlersDrain_DoesNotStartNewJobs()
    {
        var handling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new JobRuns();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerFactory>(Log);
                services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
                services.AddSingleton(probe);
                services.AddFoundatioJobs(j => j.UseStore(new InMemoryJobStore()).AddJob<ProbeJob>());
                services.AddFoundatioMessaging(m => m.UseTransport(_transport).UseServiceName("tests").AddConsumer<ProbeCommand>(async (_, _, _) =>
                {
                    handling.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                }));
            })
            .Build();

        await host.StartAsync(TestCancellationToken);
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ProbeCommand(1), cancellationToken: TestCancellationToken);
        await handling.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        // The host stops messaging first (reverse registration order) and waits for its drain before it stops the job
        // worker, which goes on claiming throughout: a job enqueued now starts during shutdown.
        var stopping = host.StopAsync(TestCancellationToken);
        await Task.Delay(300, TestCancellationToken);
        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new ProbeJobArguments(1), cancellationToken: TestCancellationToken);
        await stopping;

        Assert.Equal(0, Volatile.Read(ref probe.Started));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_WhenTheRetryPolicyThrows_StillSettlesTheMessage(bool leaseExpiration)
    {
        int invocations = 0;
        await using var transport = new InMemoryMessageTransport(new InMemoryMessageTransport().Capabilities with { LeaseExpiration = leaseExpiration });
        var policy = new RetryPolicy { MaxAttempts = 2, ShouldRetry = ex => ((ArgumentException)ex).ParamName != "id" };

        await using var consumer = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m.AddConsumer<ProbeCommand>((_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            throw new InvalidOperationException("The handler failed");
        }, new HandlerOptions { LeaseDuration = ShortLease, RetryPolicy = policy }));

        await consumer.Client.SendAsync(new ProbeCommand(1), cancellationToken: TestCancellationToken);

        // ShouldRetry throws an InvalidCastException. The processor logs "Unexpected error processing message" and stops
        // renewing without settling. With lease expiration the lease runs out and the last attempt, which CanRetry refuses
        // before it calls ShouldRetry, dead-letters it (passes, a lease late per attempt). Without lease expiration (Kafka,
        // RabbitMQ) it stays unsettled and is never delivered again: a Kafka partition's commits stop behind it.
        bool idle = await transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(6), TestCancellationToken);
        _logger.LogInformation("Handler ran {Invocations} times; idle {Idle}", Volatile.Read(ref invocations), idle);
        Assert.True(idle, $"The message was never settled; its handler ran {Volatile.Read(ref invocations)} times.");
        Assert.Equal(1, (await transport.GetStatsAsync(MessageSource.Queue(typeof(ProbeCommand).FullName!.Replace('+', '.')), TestCancellationToken))?.DeadLettered);
    }

    [Fact]
    public async Task ReplaceReceiver_WhileAHandlerOfTheFaultedReceiverRuns_KeepsReceivingOtherMessages()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new InterceptingMessageTransport(_transport);
        await using var consumer = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m
            .Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(8))
            .AddConsumer<ProbeCommand>(async (message, context, ct) =>
            {
                if (message.Id == 2)
                {
                    otherHandled.TrySetResult();
                    return;
                }

                slowStarted.TrySetResult();
                if (context.Attempt == 1)
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }, new HandlerOptions { MaxConcurrency = 4 }));

        await consumer.Client.SendAsync(new ProbeCommand(1), cancellationToken: TestCancellationToken);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        transport.FaultReceivers();
        var faultedAt = DateTime.UtcNow;
        await consumer.Client.SendAsync(new ProbeCommand(2), cancellationToken: TestCancellationToken);

        // The processor drains the faulted receiver for up to ReplaceTimeout (ShutdownTimeout, 25 s by default) before it
        // opens a new one, and receives nothing meanwhile, though three of its four slots are free.
        await otherHandled.Task.WaitAsync(TimeSpan.FromSeconds(20), TestCancellationToken);
        var waited = DateTime.UtcNow - faultedAt;
        _logger.LogInformation("Message 2 waited {Waited} for a receiver", waited);
        Assert.True(waited < TimeSpan.FromSeconds(3), $"Message 2 waited {waited} while the faulted receiver drained.");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    public async Task Stress_ReadAheadHoldsAndSlowSettlements_SettleEveryMessageOnceInGroupOrderWithinTheConcurrency(bool receiveAhead, int seed)
    {
        const int maxConcurrency = 6, groups = 12, perGroup = 40, ungrouped = 300;
        var capabilities = new InMemoryMessageTransport().Capabilities with { MaxReceiveBatchSize = 5, MaxConcurrentReceives = 3, ReceiveAhead = receiveAhead, MaxRetryDelay = TimeSpan.Zero };
        var random = new Random(seed);
        var transport = new InterceptingMessageTransport(_transport, capabilities)
        {
            OnSettleAsync = (_, _) =>
            {
                int delay;
                lock (random)
                    delay = random.Next(4);
                return delay == 0 ? Task.CompletedTask : Task.Delay(delay);
            }
        };

        var completed = new ConcurrentDictionary<int, int>();
        var lastInGroup = new ConcurrentDictionary<string, int>();
        int running = 0, maxRunning = 0, outOfOrder = 0;
        await using (var producer = await MessagingTestHost.StartAsync(_transport, Log, "tests", _ => { }))
        {
            for (int g = 0; g < groups; g++)
                await producer.Client.SendBatchAsync(Enumerable.Range(g * 1000, perGroup).Select(i => new ProbeCommand(i)), new SendOptions { GroupKey = $"g{g}" }, TestCancellationToken);

            await producer.Client.SendBatchAsync(Enumerable.Range(100_000, ungrouped).Select(i => new ProbeCommand(i)), cancellationToken: TestCancellationToken);
        }

        int barrier = 0;
        var allSlots = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var consumer = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m.AddConsumer<ProbeCommand>(async (message, context, _) =>
        {
            if (message.Id >= 200_000)
            {
                // Every slot is free again only if none leaked: all of these run at once.
                if (Interlocked.Increment(ref barrier) == maxConcurrency)
                    allSlots.TrySetResult();

                await allSlots.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                return;
            }

            InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
            try
            {
                int roll, delay;
                lock (random)
                {
                    roll = random.Next(10);
                    delay = random.Next(3);
                }

                if (delay > 0)
                    await Task.Delay(delay, CancellationToken.None);

                if (context.Attempt == 1 && roll < 2)
                {
                    context.Retry(TimeSpan.FromMilliseconds(5 + roll * 10));
                    return;
                }

                if (context.GroupKey is { } group)
                {
                    int previous = lastInGroup.GetValueOrDefault(group, -1);
                    if (message.Id < previous)
                        Interlocked.Increment(ref outOfOrder);

                    lastInGroup[group] = message.Id;
                }

                completed.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }, new HandlerOptions { MaxConcurrency = maxConcurrency, RetryPolicy = new RetryPolicy { MaxAttempts = 3 } }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(60), TestCancellationToken), $"Completed {completed.Count} of {groups * perGroup + ungrouped}");
        Assert.Equal(groups * perGroup + ungrouped, completed.Count);
        Assert.All(completed, c => Assert.Equal(1, c.Value));
        Assert.Equal(0, Volatile.Read(ref outOfOrder));
        Assert.InRange(Volatile.Read(ref maxRunning), 1, maxConcurrency);

        await consumer.Client.SendBatchAsync(Enumerable.Range(200_000, maxConcurrency).Select(i => new ProbeCommand(i)), cancellationToken: TestCancellationToken);
        await allSlots.Task.WaitAsync(TimeSpan.FromSeconds(15), TestCancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stress_FaultAndRestartMidStream_LosesNoMessageAndKeepsGroupOrder(bool receiveAhead)
    {
        const int groups = 8, perGroup = 60, ungrouped = 300, total = groups * perGroup + ungrouped;
        var capabilities = new InMemoryMessageTransport().Capabilities with { MaxReceiveBatchSize = 5, MaxConcurrentReceives = 3, ReceiveAhead = receiveAhead, MaxRetryDelay = TimeSpan.Zero };
        var random = new Random(7);
        var transport = new InterceptingMessageTransport(_transport, capabilities) { OnSettleAsync = (_, _) => Task.Delay(1) };
        await using (var producer = await MessagingTestHost.StartAsync(_transport, Log, "tests", _ => { }))
        {
            for (int g = 0; g < groups; g++)
                await producer.Client.SendBatchAsync(Enumerable.Range(g * 1000, perGroup).Select(i => new ProbeCommand(i)), new SendOptions { GroupKey = $"g{g}" }, TestCancellationToken);

            await producer.Client.SendBatchAsync(Enumerable.Range(100_000, ungrouped).Select(i => new ProbeCommand(i)), cancellationToken: TestCancellationToken);
        }

        var completed = new ConcurrentDictionary<int, int>();
        var lastInGroup = new ConcurrentDictionary<string, int>();
        int outOfOrder = 0;
        void Configure(FoundatioMessagingBuilder m) => m.Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(1)).AddConsumer<ProbeCommand>(async (message, context, ct) =>
        {
            int delay;
            lock (random)
                delay = random.Next(4);

            await Task.Delay(delay, ct);
            if (context.Attempt == 1 && delay == 3)
            {
                context.Retry(TimeSpan.FromMilliseconds(20));
                return;
            }

            if (context.GroupKey is { } group)
            {
                lock (lastInGroup)
                {
                    if (message.Id < lastInGroup.GetValueOrDefault(group, -1))
                        Interlocked.Increment(ref outOfOrder);

                    lastInGroup[group] = message.Id;
                }
            }

            completed.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        }, new HandlerOptions { MaxConcurrency = 6, RetryPolicy = new RetryPolicy { MaxAttempts = 10 } });

        await using (var first = await MessagingTestHost.StartAsync(transport, Log, "tests", Configure))
        {
            for (int i = 0; i < 400 && completed.Count < total / 4; i++)
                await Task.Delay(10, TestCancellationToken);

            transport.FaultReceivers();
            for (int i = 0; i < 400 && completed.Count < total / 2; i++)
                await Task.Delay(10, TestCancellationToken);
        }

        await using var second = await MessagingTestHost.StartAsync(transport, Log, "tests", Configure);
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(60), TestCancellationToken), $"Completed {completed.Count} of {total}");
        Assert.Equal(total, completed.Count);
        Assert.Equal(0, Volatile.Read(ref outOfOrder));
        Assert.Equal(0, (await _transport.GetStatsAsync(MessageSource.Queue(typeof(ProbeCommand).FullName!.Replace('+', '.')), TestCancellationToken))?.DeadLettered ?? 0);
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    public record ProbeCommand(int Id);
    public record ProbeJobArguments(int Id);

    public sealed class JobRuns
    {
        public int Started;
    }

    public sealed class ProbeJob(JobRuns runs) : IJobHandler<ProbeJobArguments>
    {
        public Task HandleAsync(ProbeJobArguments arguments, IJobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs.Started);
            return Task.CompletedTask;
        }
    }
}
