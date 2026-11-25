using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
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
    public async Task UseSqs_WithClientsInServicesAndNoConnectionString_UsesThem()
    {
        // Arrange
        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        var services = new ServiceCollection();
        services.AddSingleton<IAmazonSQS>(sqs);
        services.AddSingleton<IAmazonSimpleNotificationService>(sns);
        services.AddFoundatioMessaging(m => m.UseSqs());
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<SqsMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Same(sqs, transport.Sqs);
        Assert.Same(sns, transport.Sns);
    }

    [Fact]
    public async Task UseSqs_WithOnlyAnSqsClientInServices_FailsWhenTheTransportIsCreated()
    {
        // Arrange
        using var sqs = CreateSqsClient();
        var services = new ServiceCollection();
        services.AddSingleton<IAmazonSQS>(sqs);
        services.AddFoundatioMessaging(m => m.UseSqs());
        await using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Contains(nameof(IAmazonSimpleNotificationService), exception.Message);
    }

    [Fact]
    public async Task UseSqs_WithClients_UsesThemOverRegisteredOnes()
    {
        // Arrange
        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        using var registered = CreateSqsClient();
        var services = new ServiceCollection();
        services.AddSingleton<IAmazonSQS>(registered);
        services.AddFoundatioMessaging(m => m.UseSqs(sqs, sns, new SqsMessageTransportOptions { Prefix = "helpers" }));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<SqsMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Same(sqs, transport.Sqs);
        Assert.Same(sns, transport.Sns);
        Assert.Equal("helpers", transport.Options.Prefix);
    }

    [Fact]
    public async Task UseSqs_WithAConnectionString_CreatesItsOwnClientsOverRegisteredOnes()
    {
        // Arrange
        using var registered = CreateSqsClient();
        var services = new ServiceCollection();
        services.AddSingleton<IAmazonSQS>(registered);
        services.AddFoundatioMessaging(m => m.UseSqs(LocalConnectionString));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<SqsMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Equal(LocalConnectionString, transport.Options.ConnectionString);
        Assert.NotSame(registered, transport.Sqs);
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

    private const string LocalConnectionString = "serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx";

    private static AmazonSQSClient CreateSqsClient() => new(new BasicAWSCredentials("xxx", "xxx"), new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });

    private static AmazonSimpleNotificationServiceClient CreateSnsClient() => new(new BasicAWSCredentials("xxx", "xxx"), new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localhost:4566" });

    public record Ping(string Text);
}
