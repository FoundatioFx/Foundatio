using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>Starts a processor for every source that registered handlers, and drains them on shutdown.</summary>
internal sealed class MessagingHostedService : IHostedService
{
    private readonly MessagingRuntime _runtime;
    private readonly MessagingRegistrations _registrations;
    private readonly IStartupGate? _startupGate;
    private readonly CancellationTokenSource _stopping = new();
    private Task _gatedStart = Task.CompletedTask;

    public MessagingHostedService(MessagingRuntime runtime, MessagingRegistrations registrations, IServiceProvider services)
    {
        _runtime = runtime;
        _registrations = registrations;
        _startupGate = (IStartupGate?)services.GetService(typeof(IStartupGate));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_registrations.Handlers.Count == 0)
            return Task.CompletedTask;

        if (_startupGate is null)
            return StartProcessorsAsync(cancellationToken);

        _gatedStart = Task.Run(async () =>
        {
            try
            {
                if (await _startupGate.WaitAsync(_stopping.Token).AnyContext())
                    await StartProcessorsAsync(_stopping.Token).AnyContext();
                else
                    _runtime.LoggerFactory.CreateLogger<MessagingHostedService>().LogCritical("Startup failed; message handlers will not run");
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        await _gatedStart.AnyContext();
        await _runtime.StopAllAsync(cancellationToken).AnyContext();
    }

    private Task StartProcessorsAsync(CancellationToken cancellationToken)
    {
        var sources = _registrations.Handlers.GroupBy(_runtime.GetSource).ToList();
        return Task.WhenAll(sources.Select(source => _runtime.StartProcessorAsync(source.Key, source.ToList(), retryInBackground: true, cancellationToken)));
    }
}
