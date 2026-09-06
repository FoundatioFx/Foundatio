using System;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.Jobs;

public static class JobHostExtensions
{
    /// <summary>Runs registered durable job types on this host. Configure a runtime store first.</summary>
    public static IServiceCollection AddJobWorker(this IServiceCollection services, int concurrency = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        services.AddSingleton(new JobWorkerOptions { MaxConcurrency = concurrency });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobWorkerService>());
        return services;
    }

    /// <summary>Registers declared schedules and materializes due occurrences. Job execution requires AddJobWorker.</summary>
    public static IServiceCollection AddJobScheduler(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobSchedulerService>());
        return services;
    }
}
