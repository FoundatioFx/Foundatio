using System;
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

    public sealed record Ping(string Text);
}
