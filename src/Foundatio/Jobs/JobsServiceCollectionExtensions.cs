using System;
using System.Linq;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Foundatio durable jobs.</summary>
public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// Adds Foundatio jobs: <see cref="IJobClient"/> for enqueuing and tracking, plus any job types and CRON schedules
    /// you register. The worker and scheduler start with the host. Call as many times as you like; registrations accumulate.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddFoundatioJobs(j => j
    ///     .UseInMemory()
    ///     .AddJob&lt;GenerateReportJob&gt;()
    ///     .AddCronJob&lt;CleanupJob&gt;("0 * * * *"));
    /// </code>
    /// </example>
    public static IServiceCollection AddFoundatioJobs(this IServiceCollection services, Action<FoundatioJobsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registrations = services.FirstOrDefault(d => d.ServiceType == typeof(JobsRegistrations))?.ImplementationInstance as JobsRegistrations;
        if (registrations is null)
        {
            registrations = new JobsRegistrations();
            services.AddSingleton(registrations);
            services.TryAddSingleton(sp => CreateRuntime(sp, registrations));
            services.TryAddSingleton(sp => new JobClient(sp.GetRequiredService<JobsRuntime>()));
            services.TryAddSingleton<IJobClient>(sp => sp.GetRequiredService<JobClient>());
            services.TryAddSingleton(sp => new JobsStatus(sp.GetRequiredService<JobsRuntime>()));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobsHostedService>());
        }

        configure?.Invoke(new FoundatioJobsBuilder(services, registrations));
        return services;
    }

    private static JobsRuntime CreateRuntime(IServiceProvider services, JobsRegistrations registrations)
    {
        var options = new JobWorkerOptions();
        foreach (var configure in registrations.ConfigureOptions)
            configure(options);
        options.Validate();

        foreach (var schedule in registrations.Schedules.Values)
        {
            var retention = options.RetainSucceededFor < options.RetainFailedFor ? options.RetainSucceededFor : options.RetainFailedFor;
            if (schedule.Options.MisfireThreshold >= retention)
                throw new InvalidOperationException($"The misfire threshold of schedule {schedule.Name} ({schedule.Options.MisfireThreshold}) must be shorter than job retention ({retention}), or an occurrence could run twice.");
        }

        var store = services.GetService<IJobStore>()
            ?? throw new InvalidOperationException("No job store is configured. Inside AddFoundatioJobs, call UseInMemory(), UseRedis() from Foundatio.Redis, or UseStore().");

        return new JobsRuntime(store, options, registrations,
            options.Serializer ?? services.GetService<ISerializer>() ?? DefaultSerializer.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
            services.GetService<TimeProvider>() ?? TimeProvider.System)
        {
            Observer = services.GetService<IJobsObserver>()
        };
    }
}
