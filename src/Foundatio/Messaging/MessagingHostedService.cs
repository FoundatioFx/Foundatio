using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>Starts a processor for every source that registered handlers, and drains them on shutdown.</summary>
internal sealed class MessagingHostedService : IHostedService, IDisposable
{
    private readonly MessagingRuntime _runtime;
    private readonly MessagingRegistrations _registrations;
    private readonly IStartupGate? _startupGate;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task _gatedStart = Task.CompletedTask;

    public MessagingHostedService(MessagingRuntime runtime, MessagingRegistrations registrations, IServiceProvider services)
    {
        _runtime = runtime;
        _registrations = registrations;
        _startupGate = (IStartupGate?)services.GetService(typeof(IStartupGate));
        _logger = runtime.LoggerFactory.CreateLogger<MessagingHostedService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_registrations.Handlers.Count == 0)
            return Task.CompletedTask;

        if (_startupGate is null)
        {
            _runtime.HostState = ProcessingState.Running;
            return StartProcessorsAsync(cancellationToken);
        }

        _gatedStart = Task.Run(StartWhenTheGateOpensAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops every processor, even when starting them failed or was interrupted by this shutdown.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        try
        {
            await _gatedStart.AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting message handlers");
        }

        if (_runtime.HostState != ProcessingState.Faulted)
            _runtime.HostState = ProcessingState.Stopped;

        await _runtime.StopAllAsync(cancellationToken).AnyContext();
    }

    /// <summary>Ends a wait for the startup gate when the host is disposed without being stopped.</summary>
    public void Dispose() => _stopping.Cancel();

    private async Task StartWhenTheGateOpensAsync()
    {
        try
        {
            if (await _startupGate!.WaitAsync(_stopping.Token).AnyContext())
            {
                _runtime.HostState = ProcessingState.Running;
                await StartProcessorsAsync(_stopping.Token).AnyContext();
            }
            else
            {
                _runtime.HostState = ProcessingState.Faulted;
                _logger.LogCritical("Startup failed; message handlers will not run");
            }
        }
        catch (Exception ex) when (_stopping.IsCancellationRequested)
        {
            // Shutdown interrupted the start; StopAsync stops whatever did start.
            if (ex is not OperationCanceledException)
                _logger.LogWarning(ex, "Starting message handlers was interrupted by shutdown");
        }
        catch (Exception ex)
        {
            _runtime.HostState = ProcessingState.Faulted;
            _logger.LogCritical(ex, "Could not start message handlers; they will not run");
        }
    }

    private Task StartProcessorsAsync(CancellationToken cancellationToken)
    {
        var sources = _registrations.Handlers.GroupBy(_runtime.GetSource).ToList();
        return Task.WhenAll(sources.Select(source => _runtime.StartProcessorAsync(source.Key, source.ToList(), retryInBackground: true, cancellationToken)));
    }
}
