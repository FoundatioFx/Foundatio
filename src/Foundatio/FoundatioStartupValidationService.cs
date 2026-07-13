using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio;

/// <summary>
/// Fails app startup with an actionable message when a Foundatio registration cannot possibly work: CRON jobs
/// registered with no job runtime to execute them, or message handlers registered with no transport to consume from.
/// Both misconfigurations otherwise boot cleanly and silently do nothing — the most expensive kind of bug to find.
/// </summary>
internal sealed class FoundatioStartupValidationService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;

    public FoundatioStartupValidationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_serviceProvider.GetServices<ScheduledJobDefinition>().Any() && _serviceProvider.GetService<IJobRuntimeStore>() is null)
            throw new InvalidOperationException(
                "CRON jobs were registered (AddFoundatio().Jobs.AddCronJob<TJob>(...)) but no job runtime store is configured, so they would never run. " +
                "Add AddFoundatio().Jobs.UseInMemory() for development or .Jobs.UseRuntimeStore(...) for a durable store.");

        if (_serviceProvider.GetServices<MessageHandlerRegistration>().Any() && _serviceProvider.GetService<IMessageBus>() is null)
            throw new InvalidOperationException(
                "Message handlers were registered (AddFoundatio().Messaging.AddHandler<TMessage, THandler>(...)) but no message transport is configured, so they would never receive anything. " +
                "Add AddFoundatio().Messaging.UseInMemory() for development or .Messaging.UseTransport(...) for a broker.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
