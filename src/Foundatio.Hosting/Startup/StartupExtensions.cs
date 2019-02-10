using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Hosting.Startup {
    public static class StartupExtensions {
        public static IApplicationBuilder UseStartupMiddleware(this IApplicationBuilder app) {
            return app.UseMiddleware<StartupMiddleware>();
        }

        public static IServiceCollection AddStartupTaskService(this IServiceCollection services) {
            services.AddSingleton<StartupContext>();
            return services.AddHostedService<RunStartupActionsService>();
        }
    }
}