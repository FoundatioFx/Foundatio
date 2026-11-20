using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQMessagingBuilderExtensionsTests(RabbitMQTransportFixture fixture, ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task UseRabbitMQ_WithOptions_DeliversToAConsumerAndDisposesTheTransport()
    {
        // Arrange
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseRabbitMQ(new RabbitMQMessageTransportOptions { ConnectionString = fixture.ConnectionString, Prefix = $"foundatio-tests-helpers-{Guid.NewGuid():N}" })
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
        var ping = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);
        var transport = Assert.IsType<RabbitMQMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        foreach (var hostedService in Enumerable.Reverse(hostedServices))
            await hostedService.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        // Assert
        Assert.Equal("hello", ping.Text);
        Assert.Same(Log, transport.Options.LoggerFactory);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendAsync(MessageDestination.Queue("disposed"), [], TestCancellationToken));
    }

    [Fact]
    public async Task UseRabbitMQ_WithConnectionString_UsesTheDefaultOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m.UseRabbitMQ(fixture.ConnectionString));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<RabbitMQMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Equal(fixture.ConnectionString, transport.Options.ConnectionString);
        Assert.Equal(new RabbitMQMessageTransportOptions().Prefix, transport.Options.Prefix);
        Assert.Same(Log, transport.Options.LoggerFactory);
    }

    [Fact]
    public async Task UseRabbitMQ_WithoutAConnectionString_UsesTheRegisteredConnectionAndLeavesItOpen()
    {
        // Arrange
        var factory = new ConnectionFactory { Uri = new Uri(fixture.ConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(TestCancellationToken);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(connection);
        services.AddFoundatioMessaging(m => m.UseRabbitMQ(_ => new RabbitMQMessageTransportOptions { Prefix = $"foundatio-tests-helpers-{Guid.NewGuid():N}" }));
        var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<IMessageTransport>();
        var source = MessageSource.Queue($"helpers-{Guid.NewGuid():N}");

        // Act
        await using (var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
        {
            await transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "on-di-connection", Body = new byte[] { 1 } }], TestCancellationToken);
            var messages = await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(10), TestCancellationToken);
            Assert.Equal("on-di-connection", Assert.Single(messages).MessageId);
            await receiver.CompleteAsync(messages[0], TestCancellationToken);
        }

        await transport.DeleteAsync(source, TestCancellationToken);
        await provider.DisposeAsync();

        // Assert
        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task UseRabbitMQ_WithoutAConnectionStringOrConnection_FailsWithAClearError()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseRabbitMQ());
        await using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Contains("IConnection", exception.Message);
    }

    public record Ping(string Text);
}
