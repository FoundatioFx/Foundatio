using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Messaging;

/// <summary>
/// Applies the app's declared topology at startup for EVERY app with a configured transport — including publish-only
/// apps that register no handlers. Ensure creates what the routing config declares; Validate proves it exists and
/// fails boot when it doesn't (a missing destination should stop the app at startup, not surface as runtime send
/// errors); None trusts out-of-band provisioning entirely.
/// </summary>
internal sealed class MessagingTopologyStartupService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    public MessagingTopologyStartupService(IServiceProvider serviceProvider, ILoggerFactory? loggerFactory = null)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory?.CreateLogger<MessagingTopologyStartupService>() ?? NullLogger<MessagingTopologyStartupService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = (_serviceProvider.GetService(typeof(MessagingTopologyOptions)) as MessagingTopologyOptions)?.Mode ?? TopologyMode.Ensure;
        if (mode == TopologyMode.None)
            return;

        if (_serviceProvider.GetService(typeof(IMessageTopology)) is not IMessageTopology topology)
            return;

        if (mode == TopologyMode.Validate)
        {
            await topology.ValidateAsync(cancellationToken).AnyContext();
            _logger.LogInformation("Validated declared message topology");
            return;
        }

        try
        {
            await topology.EnsureAsync(cancellationToken).AnyContext();
            _logger.LogInformation("Ensured declared message topology");
        }
        catch (NotSupportedException)
        {
            // The transport cannot provision; the runtime use-time paths no-op the same way, so startup should not fail.
            _logger.LogDebug("Transport does not support topology provisioning; skipping startup ensure");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Hosts every declaratively-registered message handler for the app's lifetime: on start it launches each handler's
/// consumer/subscription; on stop it disposes them. Auto-registered when the first handler is added, so users register
/// handlers in configuration and never hand-write a hosted service. Programmatic
/// <see cref="IMessageBus.SubscribeAsync{T}"/> remain available for dynamic use.
/// </summary>
internal sealed class MessageHandlerHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnumerable<MessageHandlerRegistration> _registrations;
    private readonly ILogger _logger;
    private readonly List<IAsyncDisposable> _started = new();

    public MessageHandlerHostedService(IServiceProvider serviceProvider, IEnumerable<MessageHandlerRegistration> registrations, ILoggerFactory? loggerFactory = null)
    {
        _serviceProvider = serviceProvider;
        _registrations = registrations;
        _logger = loggerFactory?.CreateLogger<MessageHandlerHostedService>() ?? NullLogger<MessageHandlerHostedService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_registrations.Any() && _serviceProvider.GetService<IMessageBus>() is null)
            throw new InvalidOperationException("Message consumers were registered but no message transport is configured. Call AddFoundatio().Messaging.UseTransport(...) or UseInMemory().");
        try
        {
            foreach (var registration in _registrations)
            {
                var disposable = await registration.StartAsync(_serviceProvider, cancellationToken).AnyContext();
                _started.Add(disposable);
                _logger.LogInformation("Started message handler {Handler}", registration.Description);
            }
        }
        catch
        {
            // A hosted service whose StartAsync throws is not sent StopAsync, so dispose whatever we already started
            // rather than leaking those consumers' background receive loops.
            await DisposeStartedAsync().AnyContext();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => DisposeStartedAsync();

    private async Task DisposeStartedAsync()
    {
        try
        {
            // Dispose every started consumer even if one throws (e.g. a broker connection dropped mid-shutdown), so a
            // single failure can't leak the rest.
            foreach (var disposable in _started)
            {
                try
                {
                    await disposable.DisposeAsync().AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing message handler consumer: {Message}", ex.Message);
                }
            }
        }
        finally
        {
            _started.Clear();
        }
    }
}
