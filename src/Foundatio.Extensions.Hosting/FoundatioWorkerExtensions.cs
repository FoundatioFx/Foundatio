using System;
using System.Linq;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Messaging;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foundatio;

/// <summary>Configures a worker application and starts the background roles its configuration requires.</summary>
public static class FoundatioWorkerExtensions
{
    /// <summary>
    /// Configures and hosts message consumers, registered jobs, their scheduler, and delayed-message dispatch.
    /// Put the worker's Foundatio registrations in this callback. Producer-only apps use AddFoundatio instead.
    /// Individual hosting extensions remain available when roles run in separate processes.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">Transport, store, handler, and job registrations for this worker.</param>
    /// <param name="jobConcurrency">Maximum simultaneous job executions. Message concurrency is configured per consumer.</param>
    public static IServiceCollection AddFoundatioWorker(this IServiceCollection services, Action<FoundatioBuilder> configure, int? jobConcurrency = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (jobConcurrency is { } value) ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        services.TryAddSingleton<FoundatioRuntimeHealth>();
        services.AddHealthChecks().AddCheck<FoundatioHealthCheck>("foundatio");
        configure(services.AddFoundatio());

        bool handlers = services.Any(d => d.ServiceType == typeof(MessageHandlerRegistration));
        bool transport = services.Any(d => d.ServiceType == typeof(IMessageTransport));
        bool jobs = services.Any(d => d.ServiceType == typeof(JobTypeRegistration));
        bool jobStore = services.Any(d => d.ServiceType == typeof(IJobRuntimeStore));
        bool dispatchStore = jobStore || services.Any(d => d.ServiceType == typeof(IScheduledDispatchStore));

        if (handlers && !transport)
            throw new InvalidOperationException("The worker has message handlers but no transport. Configure Messaging.UseInMemory(), Messaging.UseRedis(), or Messaging.UseAws() in AddFoundatioWorker.");
        if (jobs && !jobStore)
            throw new InvalidOperationException("The worker has jobs but no runtime store. Configure Jobs.UseInMemory(), Jobs.UseRedis(), or Jobs.UseRuntimeStore(...) in AddFoundatioWorker.");

        if (transport)
            services.AddMessageConsumers();
        if (jobs)
        {
            services.AddJobWorker(jobConcurrency);
            services.AddJobScheduler();
        }
        if (transport && dispatchStore)
            services.AddScheduledMessageDispatcher();
        return services;
    }
}
