using System;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace Foundatio.RabbitMQ.Tests;

public class AspireFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<DistributedApplication>> SharedApp = new(StartAppAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private DistributedApplication? _app;
    public DistributedApplication App => _app ?? throw new InvalidOperationException("Fixture not initialized");
    public string? MessagingConnectionString { get; private set; }
    public string? MessagingDelayedConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _app = await SharedApp.Value;

        MessagingConnectionString = await _app.GetConnectionStringAsync("messaging")
            ?? throw new InvalidOperationException("Could not get messaging connection string");

        try
        {
            await _app.ResourceNotifications.WaitForResourceAsync(
                "messaging-delayed", KnownResourceStates.Running)
                .WaitAsync(TimeSpan.FromSeconds(120));

            var delayedEndpoint = _app.GetEndpoint("messaging-delayed", "amqp");
            MessagingDelayedConnectionString = $"amqp://guest:guest@{delayedEndpoint.Host}:{delayedEndpoint.Port}";
        }
        catch (TimeoutException)
        {
            MessagingDelayedConnectionString = null;
        }
    }

    private static async Task<DistributedApplication> StartAppAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Foundatio_RabbitMQ_AppHost>();

        var app = await appHost.BuildAsync();
        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.StartAsync(startCts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("messaging")
            .WaitAsync(TimeSpan.FromSeconds(120));

        for (int i = 1; i <= 3; i++)
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync($"chaos-{i}")
                .WaitAsync(TimeSpan.FromSeconds(120));
        }

        return app;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
