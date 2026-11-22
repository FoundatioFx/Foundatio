using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.AWS.Tests.Messaging;

public class SqsMessagingBuilderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    [Fact]
    public async Task UseSqs_WithOptionsFromServices_DeliversToAConsumerAndDisposesTheTransport()
    {
        // Arrange
        string? connectionString = _configuration.GetConnectionString("SQSMessageBus");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "ConnectionStrings:SQSMessageBus is not configured.");
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(_configuration);
        services.AddFoundatioMessaging(m => m
            .UseSqs(sp => new SqsMessageTransportOptions
            {
                ConnectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("SQSMessageBus"),
                Prefix = $"ft-{Guid.NewGuid():N}"[..20]
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
        var ping = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);
        var transport = Assert.IsType<SqsMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        foreach (var hostedService in Enumerable.Reverse(hostedServices))
            await hostedService.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        // Assert
        Assert.Equal("hello", ping.Text);
        Assert.Same(Log, transport.Options.LoggerFactory);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task UseSqs_WithAMessagingClock_UsesItOverTheRegisteredOne()
    {
        // Arrange
        var messagingClock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddFoundatioMessaging(m => m.UseSqs(new SqsMessageTransportOptions { ConnectionString = "serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx" }).Configure(o => o.TimeProvider = messagingClock));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<SqsMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Same(messagingClock, transport.Options.TimeProvider);
    }

    public record Ping(string Text);
}
