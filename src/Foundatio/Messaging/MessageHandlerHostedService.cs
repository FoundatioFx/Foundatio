using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

/// <summary>
/// One declarative message-handler registration: a description for logging and a factory that starts the underlying
/// queue consumer or pub/sub subscription and returns it for disposal on shutdown. Built by the <c>AddHandler</c>
/// builder methods, which bind the message type at compile time (one registration per delivery verb).
/// </summary>
internal sealed class MessageHandlerRegistration
{
    public required string Description { get; init; }
    public required Func<IServiceProvider, CancellationToken, Task<IAsyncDisposable>> StartAsync { get; init; }
}

/// <summary>The DI-selected <see cref="TopologyMode"/>, applied by the handler host at startup and by the message clients on use.</summary>
internal sealed record MessagingTopologyOptions(TopologyMode Mode);

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
        await ApplyTopologyAsync(cancellationToken).AnyContext();

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

    // Apply the app's declared topology before any handler starts consuming: Ensure creates what the routing config
    // declares, Validate proves it exists and fails startup when it doesn't (a missing destination should stop the app
    // at boot, not surface as runtime send errors), and None trusts out-of-band provisioning entirely.
    private async Task ApplyTopologyAsync(CancellationToken cancellationToken)
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
