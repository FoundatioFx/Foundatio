using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;

namespace Foundatio.Extensions.Hosting.Jobs {
    public static class JobHostExtensions {
        public static IServiceCollection AddJob(this IServiceCollection services, HostedJobOptions jobOptions) {
            if (jobOptions.JobFactory == null)
                throw new ArgumentNullException(nameof(jobOptions), "jobOptions.JobFactory is required");

            if (String.IsNullOrEmpty(jobOptions.CronSchedule)) {
                return services.AddTransient<IHostedService>(s => new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>()));
            } else {
                if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
                    services.AddTransient<IHostedService, ScheduledJobService>();

                return services.AddTransient(s => new ScheduledJobRegistration(jobOptions.JobFactory, jobOptions.CronSchedule));
            }
        }

        public static IServiceCollection AddJob(this IServiceCollection services, Func<IServiceProvider, IJob> jobFactory, HostedJobOptions jobOptions) {
            if (String.IsNullOrEmpty(jobOptions.CronSchedule)) {
                return services.AddTransient<IHostedService>(s => {
                    jobOptions.JobFactory = () => jobFactory(s);

                    return new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>());
                });
            } else {
                if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
                    services.AddTransient<IHostedService, ScheduledJobService>();

                return services.AddTransient(s => new ScheduledJobRegistration(() => jobFactory(s), jobOptions.CronSchedule));
            }
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, HostedJobOptions jobOptions) where T : class, IJob {
            services.AddTransient<T>();
            if (String.IsNullOrEmpty(jobOptions.CronSchedule)) {
                return services.AddTransient<IHostedService>(s => {
                    if (jobOptions.JobFactory == null)
                        jobOptions.JobFactory = s.GetRequiredService<T>;

                    return new HostedJobService(s, jobOptions, s.GetService<ILoggerFactory>());
                    });
            } else {
                if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
                    services.AddTransient<IHostedService, ScheduledJobService>();

                return services.AddTransient(s => new ScheduledJobRegistration(jobOptions.JobFactory ?? (s.GetRequiredService<T>), jobOptions.CronSchedule));
            }
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, bool waitForStartupActions = false) where T : class, IJob {
            return services.AddJob<T>(o => o.ApplyDefaults<T>().WaitForStartupActions(waitForStartupActions));
        }

        public static IServiceCollection AddCronJob<T>(this IServiceCollection services, string cronSchedule) where T : class, IJob {
            return services.AddJob<T>(o => o.CronSchedule(cronSchedule));
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, Action<HostedJobOptionsBuilder> configureJobOptions) where T : class, IJob {
            var jobOptionsBuilder = new HostedJobOptionsBuilder();
            configureJobOptions?.Invoke(jobOptionsBuilder);
            return services.AddJob<T>(jobOptionsBuilder.Target);
        }

        public static IServiceCollection AddJob(this IServiceCollection services, Action<HostedJobOptionsBuilder> configureJobOptions) {
            var jobOptionsBuilder = new HostedJobOptionsBuilder();
            configureJobOptions?.Invoke(jobOptionsBuilder);
            return services.AddJob(jobOptionsBuilder.Target);
        }

        public static IServiceCollection AddJob(this IServiceCollection services, Func<IServiceProvider, IJob> jobFactory, Action<HostedJobOptionsBuilder> configureJobOptions) {
            var jobOptionsBuilder = new HostedJobOptionsBuilder();
            configureJobOptions?.Invoke(jobOptionsBuilder);
            return services.AddJob(jobFactory, jobOptionsBuilder.Target);
        }

        public static IServiceCollection AddJobLifetimeService(this IServiceCollection services) {
            services.AddSingleton<ShutdownHostIfNoJobsRunningService>();
            services.AddSingleton<IHostedService>(x => x.GetRequiredService<ShutdownHostIfNoJobsRunningService>());
            return services;
        }
    }
}