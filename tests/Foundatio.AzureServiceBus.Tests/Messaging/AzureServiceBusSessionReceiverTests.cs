using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// Tests the session receiver against <see cref="FakeServiceBusClient"/>, for what needs more sessions than the emulator
/// holds, or counts and timings of broker calls.
/// </summary>
public class AzureServiceBusSessionReceiverTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private const int MaxConcurrentSessions = 8;

    [Fact]
    public async Task ReceiveAsync_WithMoreDelayedGroupsThanItParks_StillDeliversReadyGroups()
    {
        // Arrange: more sessions whose first message is due in an hour than a receiver parks, then one that is ready.
        var client = new FakeServiceBusClient();
        var dueAt = DateTimeOffset.UtcNow.AddHours(1);
        for (int i = 0; i < AzureServiceBusSessionReceiver.MaxParkedSessions + 40; i++)
            client.AddSession($"delayed-{i}", [$"delayed-{i}"], dueAt);

        client.AddSession("ready", ["ready-0"]);
        await using var transport = CreateTransport(client);
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("parked"), new ReceiverOptions(), TestCancellationToken);

        // Act
        var received = await ReceiveUntilAsync(receiver, "ready-0", TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(["ready-0"], received.Select(m => m.MessageId));
        Assert.InRange(client.LockedSessions, 1, AzureServiceBusSessionReceiver.MaxParkedSessions + MaxConcurrentSessions);
        Assert.Contains(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("parked"));
    }

    [Fact]
    public async Task ReceiveAsync_AtTheParkedSessionLimit_AcceptsAtABoundedRate()
    {
        // Arrange
        var client = new FakeServiceBusClient();
        var dueAt = DateTimeOffset.UtcNow.AddHours(1);
        for (int i = 0; i < AzureServiceBusSessionReceiver.MaxParkedSessions * 2; i++)
            client.AddSession($"delayed-{i}", [$"delayed-{i}"], dueAt);

        await using var transport = CreateTransport(client);
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("churn"), new ReceiverOptions(), TestCancellationToken);
        await ReceiveWhileAsync(receiver, () => client.LockedSessions < AzureServiceBusSessionReceiver.MaxParkedSessions, TimeSpan.FromSeconds(30));

        // Act: Service Bus keeps handing out sessions that are due later than everything parked.
        int accepts = client.Accepts;
        var window = Stopwatch.StartNew();
        await ReceiveWhileAsync(receiver, () => window.Elapsed < TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

        // Assert: it keeps accepting, at most about ten sessions a second, and never holds more than the limit.
        int acceptsPerSecond = (int)((client.Accepts - accepts) / window.Elapsed.TotalSeconds);
        Assert.InRange(acceptsPerSecond, 1, 20);
        Assert.InRange(client.LockedSessions, AzureServiceBusSessionReceiver.MaxParkedSessions, AzureServiceBusSessionReceiver.MaxParkedSessions + MaxConcurrentSessions);
    }

    [Fact]
    public async Task ReceiveAsync_FromManyOneMessageGroups_ReceivesFromSessionsConcurrentlyAndClosesThemWithoutAnEmptyReceive()
    {
        // Arrange: every receive takes 200ms, so receiving from one session after another would take seconds.
        var client = new FakeServiceBusClient { ReceiveLatency = TimeSpan.FromMilliseconds(200) };
        var sessions = Enumerable.Range(0, MaxConcurrentSessions * 2).Select(i => client.AddSession($"group-{i}", [$"group-{i}"])).ToList();
        await using var transport = CreateTransport(client);
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("groups"), new ReceiverOptions(), TestCancellationToken);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var received = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (received.Count < sessions.Count && DateTime.UtcNow < deadline)
        {
            foreach (var message in await receiver.ReceiveAsync(sessions.Count, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                received.Add(message);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        // Assert
        var elapsed = stopwatch.Elapsed;
        Assert.Equal(sessions.Select(s => s.SessionId).Order(), received.Select(m => m.MessageId).Order());
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Receiving {sessions.Count} one-message groups took {elapsed}.");
        await WaitForAsync(() => client.LockedSessions == 0, TimeSpan.FromSeconds(10));
        Assert.All(sessions, s => Assert.Equal(1, s.Receives));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task AbandonAsync_OfAGroupedMessageWhoseLaterOnesWereReceivedWithIt_RedeliversItBeforeThem(int delayMilliseconds)
    {
        // Arrange: a receive of one message gets the next ones of its group from Service Bus with it.
        var client = new FakeServiceBusClient();
        client.AddSession("a", ["a-0", "a-1", "a-2"]);
        await using var transport = CreateTransport(client);
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("abandon"), new ReceiverOptions(), TestCancellationToken);
        var first = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal("a-0", first.MessageId);

        // Act
        await receiver.AbandonAsync(first, TimeSpan.FromMilliseconds(delayMilliseconds), TestCancellationToken);
        var order = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (order.Count < 3 && DateTime.UtcNow < deadline)
        {
            foreach (var message in await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                order.Add(message);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        // Assert
        Assert.Equal(["a-0", "a-1", "a-2"], order.Select(m => m.MessageId));
        Assert.Equal([2, 1, 1], order.Select(m => m.DeliveryCount));
    }

    [Fact]
    public async Task DisposeAsync_WhileRenewalsOfParkedSessionsHang_ReturnsPromptly()
    {
        // Arrange: parked sessions whose lock renewals never answer.
        var client = new FakeServiceBusClient { HangRenewals = true, LockDuration = TimeSpan.FromSeconds(6) };
        var dueAt = DateTimeOffset.UtcNow.AddHours(1);
        for (int i = 0; i < 3; i++)
            client.AddSession($"delayed-{i}", [$"delayed-{i}"], dueAt);

        await using var transport = CreateTransport(client);
        var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("renew"), new ReceiverOptions(), TestCancellationToken);
        await ReceiveWhileAsync(receiver, () => client.Renewals < 3, TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();

        // Act
        await receiver.DisposeAsync();

        // Assert
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Disposing took {stopwatch.Elapsed}.");
        Assert.Equal(0, client.LockedSessions);
    }

    private AzureServiceBusMessageTransport CreateTransport(FakeServiceBusClient client) =>
        new(client, null, new AzureServiceBusMessageTransportOptions
        {
            CreateEntities = false,
            RequiresSession = true,
            MaxConcurrentSessions = MaxConcurrentSessions,
            LockDuration = client.LockDuration,
            LoggerFactory = Log
        });

    private async Task<IReadOnlyList<IncomingMessage>> ReceiveUntilAsync(IMessageReceiver receiver, string messageId, TimeSpan timeout)
    {
        var messages = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + timeout;
        while (!messages.Any(m => m.MessageId == messageId))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Message {messageId} did not arrive within {timeout}; received {String.Join(", ", messages.Select(m => m.MessageId))}.");

            messages.AddRange(await receiver.ReceiveAsync(16, TimeSpan.FromSeconds(1), TestCancellationToken));
        }

        return messages;
    }

    private async Task ReceiveWhileAsync(IMessageReceiver receiver, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The condition was not met in time.");

            var messages = await receiver.ReceiveAsync(16, TimeSpan.FromMilliseconds(250), TestCancellationToken);
            Assert.True(messages.Count == 0, $"Unexpectedly received message {messages.FirstOrDefault()?.MessageId}.");
        }
    }

    private async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(20, TestCancellationToken);
        }
    }
}
