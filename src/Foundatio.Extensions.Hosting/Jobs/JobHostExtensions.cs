using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

public static class JobHostExtensions
{
    public static IServiceCollection AddJob(this IServiceCollection services, HostedJobOptions jobOptions)
    {
        if (jobOptions.JobFactory == null)
            throw new ArgumentNullException(nameof(jobOptions), "jobOptions.JobFactory is required");

        if (String.IsNullOrEmpty(jobOptions.CronSchedule))
        {
            return services.AddTransient<IHostedService>(s => new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>()));
        }

        if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
            services.AddTransient<IHostedService, ScheduledJobService>();

        if (!services.Any(s => s.ServiceType == typeof(ScheduledJobManager)))
            services.AddSingleton<ScheduledJobManager>();

        return services.AddTransient(s => new ScheduledJobRegistration(jobOptions.CronSchedule, jobOptions.Name ?? Guid.NewGuid().ToString(), jobOptions.JobFactory));
    }

    public static IServiceCollection AddJob(this IServiceCollection services, Func<IServiceProvider, IJob> jobFactory, HostedJobOptions jobOptions)
    {
        if (String.IsNullOrEmpty(jobOptions.CronSchedule))
        {
            return services.AddTransient<IHostedService>(s =>
            {
                jobOptions.JobFactory = jobFactory;

                return new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>());
            });
        }

        if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
            services.AddTransient<IHostedService, ScheduledJobService>();

        if (!services.Any(s => s.ServiceType == typeof(ScheduledJobManager)))
            services.AddSingleton<ScheduledJobManager>();

        return services.AddTransient(s => new ScheduledJobRegistration(jobOptions.CronSchedule, jobOptions.Name ?? Guid.NewGuid().ToString(), _ => jobFactory(s)));
    }

    public static IServiceCollection AddJob<T>(this IServiceCollection services, HostedJobOptions jobOptions) where T : class, IJob
    {
        services.AddTransient<T>();
        if (String.IsNullOrEmpty(jobOptions.CronSchedule))
        {
            return services.AddTransient<IHostedService>(s =>
            {
                if (jobOptions.JobFactory == null)
                    jobOptions.JobFactory = _ => s.GetRequiredService<T>();

                return new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>());
            });
        }

        if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
            services.AddTransient<IHostedService, ScheduledJobService>();

        if (!services.Any(s => s.ServiceType == typeof(ScheduledJobManager)))
            services.AddSingleton<ScheduledJobManager>();

        return services.AddTransient(s => new ScheduledJobRegistration(jobOptions.CronSchedule, typeof(T).FullName, jobOptions.JobFactory ?? (_ => s.GetRequiredService<T>())));
    }

    public static IServiceCollection AddJob<T>(this IServiceCollection services, bool waitForStartupActions = false) where T : class, IJob
    {
        return services.AddJob<T>(o => o.ApplyDefaults<T>().WaitForStartupActions(waitForStartupActions));
    }

    public static IServiceCollection AddCronJob<T>(this IServiceCollection services, string cronSchedule) where T : class, IJob
    {
        return services.AddJob<T>(o => o.CronSchedule(cronSchedule));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action)
    {
        return services.AddJob(o => o.CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, action)));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string cronSchedule, Func<IServiceProvider, Task> action)
    {
        return services.AddJob(o => o.CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (xp, _) => action(xp))));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string cronSchedule, Func<Task> action)
    {
        return services.AddJob(o => o.CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, _) => action())));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string cronSchedule, Action<IServiceProvider, CancellationToken> action)
    {
        return services.AddJob(o => o.CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (xp, ct) =>
        {
            action(xp, ct);
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string cronSchedule, Action<CancellationToken> action)
    {
        return services.AddJob(o => o.CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, ct) =>
        {
            action(ct);
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string cronSchedule, Action action)
    {
        return services.AddJob(o => o.CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, _) =>
        {
            action();
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddJob<T>(this IServiceCollection services, Action<HostedJobOptionsBuilder> configureJobOptions) where T : class, IJob
    {
        var jobOptionsBuilder = new HostedJobOptionsBuilder();
        jobOptionsBuilder.Name(typeof(T).FullName);
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddJob<T>(jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddJob(this IServiceCollection services, Action<HostedJobOptionsBuilder> configureJobOptions)
    {
        var jobOptionsBuilder = new HostedJobOptionsBuilder();
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddJob(jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddJob(this IServiceCollection services, Func<IServiceProvider, IJob> jobFactory, Action<HostedJobOptionsBuilder> configureJobOptions)
    {
        var jobOptionsBuilder = new HostedJobOptionsBuilder();
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddJob(jobFactory, jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddJobLifetimeService(this IServiceCollection services)
    {
        services.AddSingleton<ShutdownHostIfNoJobsRunningService>();
        services.AddSingleton<IHostedService>(x => x.GetRequiredService<ShutdownHostIfNoJobsRunningService>());
        return services;
    }
}
