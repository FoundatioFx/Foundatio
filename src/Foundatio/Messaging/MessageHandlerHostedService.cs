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
/// queue consumer or pub/sub subscription and returns it for disposal on shutdown. Built by the
/// <c>AddQueueHandler</c>/<c>AddBroadcastHandler</c> builder methods, which bind the message type at compile time.
/// </summary>
internal sealed class MessageHandlerRegistration
{
    public required string Description { get; init; }
    public required Func<IServiceProvider, CancellationToken, Task<IAsyncDisposable>> StartAsync { get; init; }
}

/// <summary>
/// Hosts every declaratively-registered message handler for the app's lifetime: on start it launches each handler's
/// consumer/subscription; on stop it disposes them. Auto-registered when the first handler is added, so users register
/// handlers in configuration and never hand-write a hosted service. Programmatic
/// <see cref="IQueue.StartConsumerAsync{T}"/> / <see cref="IPubSub.SubscribeAsync{T}"/> remain available for dynamic use.
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
