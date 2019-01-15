using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace Foundatio.Jobs.Hosting {
    public static class JobHostExtensions {
        public static IServiceCollection AddJob<T>(this IServiceCollection services) where T : class, IJob {
            services.AddTransient<T>();
            services.AddHostedService<HostedJobService<T>>();

            return services;
        }

        public static IServiceCollection AddJobLifetime(this IServiceCollection services) {
            return services.AddSingleton<IHostLifetime, JobHostLifetime>();
        }

        public static IHostBuilder UseJobLifetime(this IHostBuilder hostBuilder) {
            return hostBuilder.ConfigureServices((hostContext, services) => services.AddJobLifetime());
        }

        public static IWebHostBuilder UseJobLifetime(this IWebHostBuilder hostBuilder) {
            return hostBuilder.ConfigureServices((hostContext, services) => services.AddJobLifetime());
        }

        public static Task RunJobHostAsync(this IHostBuilder hostBuilder, CancellationToken cancellationToken = default) {
            return hostBuilder.UseJobLifetime().Build().RunAsync(cancellationToken);
        }
    }
}