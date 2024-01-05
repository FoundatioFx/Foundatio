using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Extensions.Hosting.Startup
{
    public class StartupActionsHealthCheck : IHealthCheck
    {
        private readonly IServiceProvider _serviceProvider;

        public StartupActionsHealthCheck(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var startupContext = _serviceProvider.GetService<StartupActionsContext>();

            // no startup actions registered
            if (startupContext == null)
                return Task.FromResult(HealthCheckResult.Healthy("No startup actions registered"));

            if (startupContext.IsStartupComplete && startupContext.Result.Success)
                return Task.FromResult(HealthCheckResult.Healthy("All startup actions completed"));

            if (startupContext.IsStartupComplete && !startupContext.Result.Success)
                return Task.FromResult(HealthCheckResult.Unhealthy($"Startup action \"{startupContext.Result.FailedActionName}\" failed to complete: {startupContext.Result.ErrorMessage}"));

            return Task.FromResult(HealthCheckResult.Unhealthy("Startup actions have not completed"));
        }
    }
}
