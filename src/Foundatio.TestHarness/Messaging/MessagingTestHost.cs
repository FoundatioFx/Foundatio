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

    public IServiceProvider Services => _services;
    public IMessageClient Client => _services.GetRequiredService<IMessageClient>();

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

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(timeout.Token);

        await _services.DisposeAsync();
    }
}
