using System;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Messaging;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class ScheduledMessageDispatcherTests
{
    [Fact]
    public async Task HostedDispatcher_WithOnlyDispatchStore_DrainsMessagesAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IScheduledDispatchStore>(store);
        services.AddFoundatio().Messaging.UseInMemory();
        services.AddScheduledMessageDispatcher();
        services.AddScheduledMessageDispatcher();
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IJobRuntimeStore>());
        var destination = DestinationAddress.ForQueue("hosted-dispatch");
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "hosted",
            Destination = destination,
            DueUtc = DateTimeOffset.UtcNow,
            Body = "hello"u8.ToArray()
        }, token);
        var host = Assert.Single(provider.GetServices<IHostedService>());
        await host.StartAsync(token);
        try
        {
            var pull = Assert.IsAssignableFrom<ISupportsPull>(provider.GetRequiredService<IMessageTransport>());
            var received = await pull.ReceiveAsync(destination, new ReceiveRequest { MaxMessages = 1, MaxWaitTime = TimeSpan.FromSeconds(5) }, token);
            Assert.Equal("hosted", Assert.Single(received).ApplicationMessageId);
        }
        finally
        {
            await host.StopAsync(token);
        }
    }

    [Fact]
    public async Task DispatchDueAsync_WithOnlyMessagingDependencies_SendsAndRetiresDueMessages()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        await using var transport = new InMemoryMessageTransport();
        var destination = DestinationAddress.ForQueue("scheduled-work");
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-1",
            Destination = destination,
            DueUtc = time.GetUtcNow(),
            Body = "hello"u8.ToArray(),
            Headers = MessageHeaders.Create(new System.Collections.Generic.Dictionary<string, string>
            {
                [KnownHeaders.MessageId] = "application-1",
                [KnownHeaders.ContentType] = "text/plain"
            })
        }, token);
        var dispatcher = new ScheduledMessageDispatcher(store, transport, new ScheduledMessageDispatcherOptions { TimeProvider = time });

        Assert.Equal(1, await dispatcher.DispatchDueAsync(cancellationToken: token));
        Assert.Equal(0, await dispatcher.DispatchDueAsync(cancellationToken: token));
        var entry = Assert.Single(await transport.ReceiveAsync(destination, new ReceiveRequest { MaxMessages = 1 }, token));
        Assert.Equal("application-1", entry.ApplicationMessageId);
        Assert.Equal("text/plain", entry.ContentType);
        Assert.Equal("hello"u8.ToArray(), entry.Body.ToArray());
    }

    [Fact]
    public async Task DispatchDueAsync_InValidateMode_DoesNotCreateMissingDestination()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        await using var transport = new InMemoryMessageTransport();
        var destination = DestinationAddress.ForQueue("missing");
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "missing-destination",
            Destination = destination,
            Body = ReadOnlyMemory<byte>.Empty,
            DueUtc = time.GetUtcNow()
        }, token);
        var dispatcher = new ScheduledMessageDispatcher(store, transport, new ScheduledMessageDispatcherOptions
        {
            TimeProvider = time,
            TopologyMode = TopologyMode.Validate
        });

        Assert.Equal(0, await dispatcher.DispatchDueAsync(cancellationToken: token));
        Assert.False(await transport.ExistsAsync(destination, token));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 1, "new-claim", TimeSpan.FromMinutes(1), token));
    }
}
