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

        return services.AddTransient<IHostedService>(s => new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>()));
    }

    public static IServiceCollection AddJob<T>(this IServiceCollection services, HostedJobOptions jobOptions = null) where T : class, IJob
    {
        services.AddTransient<T>();
        return services.AddTransient<IHostedService>(s =>
        {
            if (jobOptions == null)
            {
                jobOptions = new HostedJobOptions();
                jobOptions.ApplyDefaults<T>();
            }

            jobOptions.Name ??= typeof(T).FullName;
            jobOptions.JobFactory ??= sp => sp.GetRequiredService<T>();

            return new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>());
        });
    }

    public static IServiceCollection AddJob<T>(this IServiceCollection services, Action<HostedJobOptionsBuilder> configureJobOptions) where T : class, IJob
    {
        var jobOptionsBuilder = new HostedJobOptionsBuilder();
        jobOptionsBuilder.ApplyDefaults<T>();
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

    public static IServiceCollection AddJob(this IServiceCollection services, string name, Func<IServiceProvider, IJob> jobFactory, Action<HostedJobOptionsBuilder> configureJobOptions)
    {
        var jobOptionsBuilder = new HostedJobOptionsBuilder();
        jobOptionsBuilder.Name(name).JobFactory(jobFactory);
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddJob(jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, ScheduledJobOptions jobOptions)
    {
        if (jobOptions.JobFactory == null)
            throw new ArgumentNullException(nameof(jobOptions), "jobOptions.JobFactory is required");

        services.AddJobScheduler();

        return services.AddTransient(s => new ScheduledJobRegistration(jobOptions));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, Action<ScheduledJobOptionsBuilder> configureJobOptions)
    {
        var jobOptionsBuilder = new ScheduledJobOptionsBuilder();
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddCronJob(jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddCronJob<T>(this IServiceCollection services, string cronSchedule, Action<ScheduledJobOptionsBuilder> configureJobOptions = null) where T : class, IJob
    {
        services.AddTransient<T>();
        var jobOptionsBuilder = new ScheduledJobOptionsBuilder();
        jobOptionsBuilder.Name(typeof(T).FullName).CronSchedule(cronSchedule).JobFactory(sp => sp.GetRequiredService<T>());
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddCronJob(jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string name, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action)
    {
        return services.AddCronJob(o => o.Name(name).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, action)));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string name, string cronSchedule, Func<IServiceProvider, Task> action)
    {
        return services.AddCronJob(o => o.Name(name).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (xp, _) => action(xp))));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string name, string cronSchedule, Func<Task> action)
    {
        return services.AddCronJob(o => o.Name(name).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, _) => action())));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string name, string cronSchedule, Action<IServiceProvider, CancellationToken> action)
    {
        return services.AddCronJob(o => o.Name(name).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (xp, ct) =>
        {
            action(xp, ct);
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string name, string cronSchedule, Action<CancellationToken> action)
    {
        return services.AddCronJob(o => o.Name(name).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, ct) =>
        {
            action(ct);
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddCronJob(this IServiceCollection services, string name, string cronSchedule, Action action)
    {
        return services.AddCronJob(o => o.Name(name).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, _) =>
        {
            action();
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddDistributedCronJob<T>(this IServiceCollection services, string cronSchedule, Action<ScheduledJobOptionsBuilder> configureJobOptions = null) where T : class, IJob
    {
        services.AddTransient<T>();
        var jobOptionsBuilder = new ScheduledJobOptionsBuilder();
        jobOptionsBuilder.Name(typeof(T).FullName).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => sp.GetRequiredService<T>());
        configureJobOptions?.Invoke(jobOptionsBuilder);
        return services.AddCronJob(jobOptionsBuilder.Target);
    }

    public static IServiceCollection AddDistributedCronJob(this IServiceCollection services, string name, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action)
    {
        return services.AddCronJob(o => o.Name(name).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, action)));
    }

    public static IServiceCollection AddDistributedCronJob(this IServiceCollection services, string name, string cronSchedule, Func<IServiceProvider, Task> action)
    {
        return services.AddCronJob(o => o.Name(name).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (xp, _) => action(xp))));
    }

    public static IServiceCollection AddDistributedCronJob(this IServiceCollection services, string name, string cronSchedule, Func<Task> action)
    {
        return services.AddCronJob(o => o.Name(name).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, _) => action())));
    }

    public static IServiceCollection AddDistributedCronJob(this IServiceCollection services, string name, string cronSchedule, Action<IServiceProvider, CancellationToken> action)
    {
        return services.AddCronJob(o => o.Name(name).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (xp, ct) =>
        {
            action(xp, ct);
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddDistributedCronJob(this IServiceCollection services, string name, string cronSchedule, Action<CancellationToken> action)
    {
        return services.AddCronJob(o => o.Name(name).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, ct) =>
        {
            action(ct);
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddDistributedCronJob(this IServiceCollection services, string name, string cronSchedule, Action action)
    {
        return services.AddCronJob(o => o.Name(name).Distributed(true).CronSchedule(cronSchedule).JobFactory(sp => new DynamicJob(sp, (_, _) =>
        {
            action();
            return Task.CompletedTask;
        })));
    }

    public static IServiceCollection AddJobScheduler(this IServiceCollection services)
    {
        if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
            services.AddTransient<IHostedService, ScheduledJobService>();

        if (!services.Any(s => s.ServiceType == typeof(ScheduledJobManager) && s.ImplementationType == typeof(ScheduledJobManager)))
            services.AddSingleton<ScheduledJobManager>();

        if (!services.Any(s => s.ServiceType == typeof(IScheduledJobManager) && s.ImplementationType == typeof(ScheduledJobManager)))
            services.AddSingleton<IScheduledJobManager>(sp => sp.GetRequiredService<ScheduledJobManager>());

        return services;
    }

    public static IServiceCollection AddJobLifetimeService(this IServiceCollection services)
    {
        services.AddSingleton<ShutdownHostIfNoJobsRunningService>();
        services.AddSingleton<IHostedService>(x => x.GetRequiredService<ShutdownHostIfNoJobsRunningService>());
        return services;
    }
}
