using System;
using System.Linq;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.Jobs;

public static class JobHostExtensions
{
    /// <summary>Runs registered durable job types on this host. Configure a runtime store first.</summary>
    public static IServiceCollection AddJobWorker(this IServiceCollection services, int? concurrency = null)
    {
        if (concurrency is { } value) ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        var options = services.LastOrDefault(d => d.ServiceType == typeof(JobWorkerOptions))?.ImplementationInstance as JobWorkerOptions ?? new();
        services.Replace(ServiceDescriptor.Singleton(options with { MaxConcurrency = concurrency ?? options.MaxConcurrency }));
        services.TryAddSingleton<FoundatioRuntimeHealth>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobWorkerService>());
        return services;
    }

    /// <summary>Registers declared schedules and materializes due occurrences. Job execution requires AddJobWorker.</summary>
    public static IServiceCollection AddJobScheduler(this IServiceCollection services)
    {
        services.TryAddSingleton<FoundatioRuntimeHealth>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobSchedulerService>());
        return services;
    }
}
