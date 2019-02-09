using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Foundatio.Jobs;

namespace Foundatio.Hosting.Jobs {
    public static class JobHostExtensions {
        public static IServiceCollection AddJob<T>(this IServiceCollection services) where T : class, IJob {
            services.AddTransient<T>();
            services.AddHostedService<HostedJobService<T>>();

            return services;
        }

        public static IServiceCollection AddJobLifetime(this IServiceCollection services) {
            services.AddSingleton<JobHostLifetime>();
            return services.AddSingleton<IHostedService>(s => s.GetService<JobHostLifetime>());
        }

        public static IWebHostBuilder UseJobLifetime(this IWebHostBuilder hostBuilder) {
            return hostBuilder.ConfigureServices((hostContext, services) => services.AddJobLifetime());
        }

        public static void RunJobHost(this IWebHostBuilder hostBuilder, CancellationToken cancellationToken = default) {
            hostBuilder.UseJobLifetime().Build().Run();
        }

        public static IHealthChecksBuilder AddJobCheck<T>(this IHealthChecksBuilder builder, IEnumerable<string> tags = null) where T : class, IHealthCheck {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            return builder.Add(new HealthCheckRegistration(nameof(T), ActivatorUtilities.GetServiceOrCreateInstance<T>, HealthStatus.Unhealthy, tags));
        }

    }
}