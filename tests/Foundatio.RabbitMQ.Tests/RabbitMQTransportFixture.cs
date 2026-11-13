using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(Foundatio.RabbitMQ.Tests.RabbitMQTransportFixture))]

namespace Foundatio.RabbitMQ.Tests;

/// <summary>
/// One RabbitMQ broker, with the management plugin, for the message transport tests: lighter than the AppHost's
/// brokers and chaos cluster, and stopped, with its container, when the test run ends. Set
/// <c>RABBITMQ_TRANSPORT_CONNECTION_STRING</c> (and <c>RABBITMQ_TRANSPORT_MANAGEMENT_URI</c>) to use a broker of your
/// own instead, or <c>RABBITMQ_TRANSPORT_IMAGE_TAG</c> to start another version, for example "4.2-management".
/// </summary>
public sealed class RabbitMQTransportFixture : IAsyncLifetime
{
    private DistributedApplication? _app;

    public string ConnectionString { get; private set; } = String.Empty;

    public Uri? ManagementEndpoint { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RABBITMQ_TRANSPORT_CONNECTION_STRING") is { Length: > 0 } connectionString)
        {
            ConnectionString = connectionString;
            ManagementEndpoint = Environment.GetEnvironmentVariable("RABBITMQ_TRANSPORT_MANAGEMENT_URI") is { Length: > 0 } management ? new Uri(management) : null;
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // The AppHost only supplies Aspire's settings here: its brokers and chaos cluster are removed.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Foundatio_RabbitMQ_AppHost>(timeout.Token);
        foreach (var resource in builder.Resources.ToList())
            builder.Resources.Remove(resource);

        var broker = builder.AddRabbitMQ("transport").WithManagementPlugin();
        if (Environment.GetEnvironmentVariable("RABBITMQ_TRANSPORT_IMAGE_TAG") is { Length: > 0 } tag)
            broker.WithImageTag(tag);

        _app = await builder.BuildAsync(timeout.Token);
        await _app.StartAsync(timeout.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("transport", timeout.Token);

        ConnectionString = await _app.GetConnectionStringAsync("transport", timeout.Token)
            ?? throw new InvalidOperationException("The RabbitMQ resource has no connection string.");
        ManagementEndpoint = _app.GetEndpoint("transport", "management");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
