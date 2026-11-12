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
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Leases, settlement and lifecycle of the processors that run message handlers.</summary>
public class MessageProcessorTests : TestWithLoggingBase
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(1);
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
    public async Task Receive_WhenIdle_LongPollsAndStillStopsPromptly()
    {
        var waits = new ConcurrentQueue<TimeSpan>();
        var transport = new InterceptingMessageTransport(_transport) { OnReceive = (_, _, wait) => waits.Enqueue(wait) };
        var consumer = await StartAsync(transport, m => m.AddConsumer<RenewedCommand>((_, _, _) => Task.CompletedTask));
        await WaitForAsync(() => !waits.IsEmpty, TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await consumer.DisposeAsync();

        Assert.Equal(TimeSpan.FromSeconds(20), waits.First());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Stopping took {stopwatch.Elapsed}.");
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task<MessagingTestHost> StartAsync(IMessageTransport transport, Action<FoundatioMessagingBuilder> configure)
        => MessagingTestHost.StartAsync(transport, Log, "tests", configure);

    private static string QueueName<T>() => typeof(T).FullName!.Replace('+', '.');

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
}
