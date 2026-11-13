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
    private static readonly Lazy<Task<DistributedApplication?>> SharedApp = new(StartAppAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private DistributedApplication? _app;
    public DistributedApplication App => _app ?? throw new InvalidOperationException("Fixture not initialized - Aspire AppHost failed to start");
    public string? MessagingConnectionString { get; private set; }
    public string? MessagingDelayedConnectionString { get; private set; }
    public bool ChaosClusterAvailable { get; private set; }
    public bool IsAvailable => _app is not null && MessagingConnectionString is not null;

    public async ValueTask InitializeAsync()
    {
        _app = await SharedApp.Value;
        if (_app is null)
            return;

        MessagingConnectionString = await _app.GetConnectionStringAsync("messaging");
        if (MessagingConnectionString is null)
            return;

        try
        {
            await _app.ResourceNotifications.WaitForResourceAsync(
                "messaging-delayed", KnownResourceStates.Running)
                .WaitAsync(TimeSpan.FromSeconds(60));

            var delayedEndpoint = _app.GetEndpoint("messaging-delayed", "amqp");
            MessagingDelayedConnectionString = $"amqp://guest:guest@{delayedEndpoint.Host}:{delayedEndpoint.Port}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            MessagingDelayedConnectionString = null;
        }

        try
        {
            await Task.WhenAll(
                _app.ResourceNotifications.WaitForResourceHealthyAsync("chaos-1")
                    .WaitAsync(TimeSpan.FromSeconds(30)),
                _app.ResourceNotifications.WaitForResourceHealthyAsync("chaos-2")
                    .WaitAsync(TimeSpan.FromSeconds(30)),
                _app.ResourceNotifications.WaitForResourceHealthyAsync("chaos-3")
                    .WaitAsync(TimeSpan.FromSeconds(30))
            );
            ChaosClusterAvailable = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ChaosClusterAvailable = false;
        }
    }

    private static async Task<DistributedApplication?> StartAppAsync()
    {
        try
        {
            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.Foundatio_RabbitMQ_AppHost>()
                .WaitAsync(TimeSpan.FromMinutes(5));

            var app = await appHost.BuildAsync()
                .WaitAsync(TimeSpan.FromMinutes(1));

            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await app.StartAsync(startCts.Token);

            await app.ResourceNotifications.WaitForResourceHealthyAsync("messaging")
                .WaitAsync(TimeSpan.FromSeconds(120));

            return app;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
