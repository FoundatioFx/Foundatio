using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Messaging;

/// <summary>
/// A minimal host for tests: one service's messaging configuration over a shared transport. Start several with the
/// same transport to simulate several services or replicas.
/// </summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed class MessagingTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly IReadOnlyList<IHostedService> _hostedServices;

    private MessagingTestHost(ServiceProvider services, IReadOnlyList<IHostedService> hostedServices)
    {
        _services = services;
        _hostedServices = hostedServices;
    }

    /// <summary>The host's service provider.</summary>
    public IServiceProvider Services => _services;
    /// <summary>The host's message client, for sending and publishing as this service.</summary>
    public IMessageClient Client => _services.GetRequiredService<IMessageClient>();

    /// <summary>Starts a host named <paramref name="serviceName"/> on <paramref name="transport"/>, which the caller disposes.</summary>
    /// <param name="transport">The transport, shared with the other hosts of the test.</param>
    /// <param name="loggerFactory">Receives the host's logs.</param>
    /// <param name="serviceName">The service name of the host's durable subscriptions.</param>
    /// <param name="configure">Registers the host's handlers and messaging settings.</param>
    /// <param name="configureServices">Registers other services the handlers need.</param>
    public static async Task<MessagingTestHost> StartAsync(IMessageTransport transport, ILoggerFactory loggerFactory, string serviceName,
        Action<FoundatioMessagingBuilder> configure, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        configureServices?.Invoke(services);
        services.AddFoundatioMessaging(m =>
        {
            m.UseTransport(transport).UseServiceName(serviceName).Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            configure(m);
        });

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new MessagingTestHost(provider, hostedServices);
    }

    /// <summary>Stops the host, allowing up to 10 seconds, and disposes its services. The transport stays open.</summary>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(timeout.Token);

        await _services.DisposeAsync();
    }
}
