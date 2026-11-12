using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessagingBuilderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task UseKafka_WithOptions_DeliversToAConsumerAndDisposesTheTransport()
    {
        // Arrange
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseKafka(new KafkaMessageTransportOptions
            {
                BootstrapServers = KafkaTestConnection.BootstrapServers,
                Prefix = $"foundatio-tests-helpers-{Guid.NewGuid():N}",
                Partitions = 1,
                ReplicationFactor = 1,
                ConfigureConsumer = KafkaTestConnection.UseConsumerGroupProtocol ? c => c.GroupProtocol = GroupProtocol.Consumer : null
            })
            .UseServiceName("helpers")
            .AddConsumer<Ping>((ping, _, _) =>
            {
                received.TrySetResult(ping);
                return Task.CompletedTask;
            }));
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(TestCancellationToken);

        // Act
        await provider.GetRequiredService<IMessageClient>().SendAsync(new Ping("hello"), cancellationToken: TestCancellationToken);
        var ping = await received.Task.WaitAsync(TimeSpan.FromSeconds(60), TestCancellationToken);
        var transport = Assert.IsType<KafkaMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        foreach (var hostedService in Enumerable.Reverse(hostedServices))
            await hostedService.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        // Assert
        Assert.Equal("hello", ping.Text);
        Assert.Same(Log, transport.Options.LoggerFactory);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendAsync(MessageDestination.Queue("disposed"), [], TestCancellationToken));
    }

    [Fact]
    public async Task UseKafka_WithBootstrapServers_UsesTheDefaultOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m.UseKafka(KafkaTestConnection.BootstrapServers));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<KafkaMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Equal(KafkaTestConnection.BootstrapServers, transport.Options.BootstrapServers);
        Assert.Equal(new KafkaMessageTransportOptions { BootstrapServers = "x" }.Prefix, transport.Options.Prefix);
        Assert.Same(Log, transport.Options.LoggerFactory);
    }

    public record Ping(string Text);
}
