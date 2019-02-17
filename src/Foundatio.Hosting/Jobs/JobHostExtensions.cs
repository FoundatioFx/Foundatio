using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;

namespace Foundatio.Hosting.Jobs {
    public static class JobHostExtensions {
        public static IServiceCollection AddJob<T>(this IServiceCollection services, HostedJobOptions jobOptions) where T : class, IJob {
            services.AddTransient<T>();
            if (String.IsNullOrEmpty(jobOptions.CronSchedule)) {
                return services.AddTransient<IHostedService>(s => {
                    if (jobOptions.JobFactory == null)
                        jobOptions.JobFactory = () => s.GetRequiredService<T>();

                    return new HostedJobService<T>(s, jobOptions, s.GetService<ILoggerFactory>());
                    });
            } else {
                if (!services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ScheduledJobService)))
                    services.AddTransient<IHostedService, ScheduledJobService>();

                return services.AddTransient(s => new ScheduledJobRegistration(jobOptions.JobFactory ?? (() => s.GetRequiredService<T>()), jobOptions.CronSchedule));
            }
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, bool waitForStartupActions = false) where T : class, IJob {
            return services.AddJob<T>(o => o.ApplyDefaults().WaitForStartupActions(waitForStartupActions));
        }

        public static IServiceCollection AddCronJob<T>(this IServiceCollection services, string cronSchedule) where T : class, IJob {
            return services.AddJob<T>(o => o.CronSchedule(cronSchedule));
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, Action<HostedJobOptionsBuilder<T>> configureJobOptions) where T : class, IJob {
            var jobOptionsBuilder = new HostedJobOptionsBuilder<T>();
            configureJobOptions?.Invoke(jobOptionsBuilder);
            return services.AddJob<T>(jobOptionsBuilder.Target);
        }

        public static IServiceCollection AddJobLifetimeService(this IServiceCollection services) {
            return services.AddSingleton<ShutdownHostIfNoJobsRunningService>();
        }
    }
}