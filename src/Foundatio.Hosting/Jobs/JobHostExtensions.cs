using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;
using System;

namespace Foundatio.Hosting.Jobs {
    public static class JobHostExtensions {
        public static IServiceCollection AddJob<T>(this IServiceCollection services, bool waitForStartupActions = false) where T : class, IJob {
            services.AddTransient<T>();
            var jobOptions = new HostedJobOptions();
            jobOptions.ApplyDefaults<T>();
            jobOptions.WaitForStartupActions = waitForStartupActions;
            return services.AddTransient<IHostedService>(s => new HostedJobService<T>(s, jobOptions, s.GetService<ILoggerFactory>()));
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, HostedJobOptions jobOptions) where T : class, IJob {
            services.AddTransient<T>();
            return services.AddTransient<IHostedService>(s => new HostedJobService<T>(s, jobOptions, s.GetService<ILoggerFactory>()));
        }

        public static IServiceCollection AddJob<T>(this IServiceCollection services, Action<HostedJobOptionsBuilder<T>> configureJobOptions) where T : class, IJob {
            services.AddTransient<T>();
            var jobOptionsBuilder = new HostedJobOptionsBuilder<T>();
            configureJobOptions?.Invoke(jobOptionsBuilder);
            return services.AddTransient<IHostedService>(s => new HostedJobService<T>(s, jobOptionsBuilder.Target, s.GetService<ILoggerFactory>()));
        }

        public static IServiceCollection AddJobLifetimeService(this IServiceCollection services) {
            return services.AddSingleton<ShutdownHostIfNoJobsRunningService>();
        }
    }
}