using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

public class AzureServiceBusMessagingBuilderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private readonly string _prefix = $"fb{Guid.NewGuid():N}"[..10] + "-";

    [Fact]
    public async Task UseAzureServiceBus_WithOptionsFromServices_DeliversToAConsumerAndDisposesTheTransport()
    {
        // Arrange
        string connectionString = GetConnectionString();
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseAzureServiceBus(_ => new AzureServiceBusMessageTransportOptions { ConnectionString = connectionString, Prefix = _prefix, MaxDeliveryCount = 10 })
            .UseServiceName("helpers")
            .AddConsumer<Ping>((ping, _, _) =>
            {
                received.TrySetResult(ping);
                return Task.CompletedTask;
            }));

        // Act
        var (ping, transport) = await RunAsync(services, provider => provider.GetRequiredService<IMessageClient>().SendAsync(new Ping("hello"), cancellationToken: TestCancellationToken), received.Task);

        // Assert
        Assert.Equal("hello", ping.Text);
        Assert.Same(Log, transport.Options.LoggerFactory);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task UseAzureServiceBus_WithClientsFromServices_UsesThemAndLeavesThemOpen()
    {
        // Arrange
        string connectionString = GetConnectionString();
        await using var client = new ServiceBusClient(connectionString);
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(client);
        services.AddSingleton(new ServiceBusAdministrationClient(AzureServiceBusMessageTransport.GetAdministrationConnectionString(connectionString)));
        services.AddFoundatioMessaging(m => m
            .UseAzureServiceBus(new AzureServiceBusMessageTransportOptions { Prefix = _prefix, MaxDeliveryCount = 10 })
            .UseServiceName("helpers")
            .AddSubscriber<Ping>((ping, _, _) =>
            {
                received.TrySetResult(ping);
                return Task.CompletedTask;
            }));

        // Act
        var (ping, transport) = await RunAsync(services, provider => provider.GetRequiredService<IMessageClient>().PublishAsync(new Ping("from DI"), cancellationToken: TestCancellationToken), received.Task);

        // Assert
        Assert.Equal("from DI", ping.Text);
        Assert.Same(client, transport.Client);
        Assert.True(transport.IsDisposed);
        Assert.False(client.IsClosed);
    }

    [Fact]
    public void UseAzureServiceBus_WithoutAConnectionOrClient_FailsWhenTheTransportIsCreated()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseAzureServiceBus());
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Contains("ServiceBusClient", exception.Message);
    }

    [Fact]
    public async Task UseAzureServiceBus_WithAClientButNoAdministrationClient_NeedsCreateEntitiesOff()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dg=="));
        services.AddFoundatioMessaging(m => m.UseAzureServiceBus());
        await using var provider = services.BuildServiceProvider();
        var limited = new ServiceCollection();
        limited.AddSingleton(new ServiceBusClient("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dg=="));
        limited.AddFoundatioMessaging(m => m.UseAzureServiceBus(new AzureServiceBusMessageTransportOptions { CreateEntities = false }));
        await using var limitedProvider = limited.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageTransport>());
        var transport = limitedProvider.GetRequiredService<IMessageTransport>();

        // Assert
        Assert.Contains("ServiceBusAdministrationClient", exception.Message);
        Assert.IsType<AzureServiceBusMessageTransport>(transport);
    }

    private async Task<(Ping Ping, AzureServiceBusMessageTransport Transport)> RunAsync(ServiceCollection services, Func<IServiceProvider, Task> act, Task<Ping> received)
    {
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var transport = Assert.IsType<AzureServiceBusMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        try
        {
            foreach (var hostedService in hostedServices)
                await hostedService.StartAsync(TestCancellationToken);

            await act(provider);
            var ping = await received.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);
            foreach (var hostedService in Enumerable.Reverse(hostedServices))
                await hostedService.StopAsync(CancellationToken.None);

            return (ping, transport);
        }
        finally
        {
            await provider.DisposeAsync();
            await DeleteEntitiesAsync();
        }
    }

    private async Task DeleteEntitiesAsync()
    {
        var admin = new ServiceBusAdministrationClient(AzureServiceBusMessageTransport.GetAdministrationConnectionString(GetConnectionString()));
        var queues = new List<string>();
        await foreach (var queue in admin.GetQueuesAsync())
        {
            if (queue.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                queues.Add(queue.Name);
        }

        var topics = new List<string>();
        await foreach (var topic in admin.GetTopicsAsync())
        {
            if (topic.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                topics.Add(topic.Name);
        }

        foreach (string queue in queues)
            await admin.DeleteQueueAsync(queue);
        foreach (string topic in topics)
            await admin.DeleteTopicAsync(topic);
    }

    private static string GetConnectionString()
    {
        string? connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "ConnectionStrings:AzureServiceBusConnectionString is not configured.");
        return connectionString;
    }

    public record Ping(string Text);
}
