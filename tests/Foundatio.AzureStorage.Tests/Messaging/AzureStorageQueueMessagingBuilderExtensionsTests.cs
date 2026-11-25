using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureStorage.Tests.Messaging;

public class AzureStorageQueueMessagingBuilderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task UseAzureStorageQueues_WithAConnectionString_DeliversToAConsumer()
    {
        // Arrange
        string prefix = $"ft{Guid.NewGuid():N}"[..10] + "-";
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseAzureStorageQueues(new AzureStorageQueueMessageTransportOptions { ConnectionString = AzureStorageQueueMessageTransportTests.GetConnectionString(), Prefix = prefix })
            .UseServiceName("helpers")
            .AddConsumer<Ping>((ping, _, _) =>
            {
                received.TrySetResult(ping);
                return Task.CompletedTask;
            }));
        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(TestCancellationToken);

        try
        {
            // Act
            await provider.GetRequiredService<IMessageClient>().SendAsync(new Ping("hello"), cancellationToken: TestCancellationToken);
            var ping = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);

            // Assert
            Assert.Equal("hello", ping.Text);
            Assert.IsType<AzureStorageQueueMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        }
        finally
        {
            foreach (var hostedService in Enumerable.Reverse(hostedServices))
                await hostedService.StopAsync(TestCancellationToken);

            var service = new QueueServiceClient(AzureStorageQueueMessageTransportTests.GetConnectionString());
            await foreach (var queue in service.GetQueuesAsync(prefix: prefix, cancellationToken: TestCancellationToken))
                await service.DeleteQueueAsync(queue.Name, TestCancellationToken);
        }
    }

    [Fact]
    public async Task AddLegacyMessageBus_WhenTheHostStops_DeletesItsInstanceSubscriptionQueue()
    {
        // Arrange
        string prefix = $"fl{Guid.NewGuid():N}"[..10] + "-";
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseAzureStorageQueues(new AzureStorageQueueMessageTransportOptions { ConnectionString = AzureStorageQueueMessageTransportTests.GetConnectionString(), Prefix = prefix })
            .UseServiceName("helpers")
            .AddLegacyMessageBus());
        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(TestCancellationToken);

        var service = new QueueServiceClient(AzureStorageQueueMessageTransportTests.GetConnectionString());
        try
        {
            await provider.GetRequiredService<IMessageBus>().SubscribeAsync<Ping>(_ => { }, TestCancellationToken);
            Assert.Single(await ListQueuesAsync(service, prefix), name => name.Contains("-legacy-bus-", StringComparison.Ordinal));

            // Act
            foreach (var hostedService in Enumerable.Reverse(hostedServices))
                await hostedService.StopAsync(TestCancellationToken);

            // Assert
            Assert.Empty(await ListQueuesAsync(service, prefix));
        }
        finally
        {
            foreach (string queue in await ListQueuesAsync(service, prefix))
                await service.DeleteQueueAsync(queue, TestCancellationToken);
        }
    }

    [Fact]
    public async Task UseAzureStorageQueues_WithoutAConnection_UsesTheRegisteredQueueServiceClient()
    {
        var service = new QueueServiceClient("UseDevelopmentStorage=true");
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddFoundatioMessaging(m => m.UseAzureStorageQueues());
        await using var provider = services.BuildServiceProvider();

        var transport = Assert.IsType<AzureStorageQueueMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        Assert.Same(service, transport.Service);
    }

    [Fact]
    public void UseAzureStorageQueues_WithoutAConnectionOrClient_ThrowsAndSaysWhatToSet()
    {
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseAzureStorageQueues());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageTransport>());

        Assert.Contains("QueueServiceClient", ex.Message, StringComparison.Ordinal);
    }

    private async Task<List<string>> ListQueuesAsync(QueueServiceClient service, string prefix)
    {
        var names = new List<string>();
        await foreach (var queue in service.GetQueuesAsync(prefix: prefix, cancellationToken: TestCancellationToken))
            names.Add(queue.Name);

        return names;
    }

    public sealed record Ping(string Text);
}
