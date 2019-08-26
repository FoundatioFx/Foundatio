using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Hosting.Startup {
    public class StartupActionsHealthCheck : IHealthCheck {
        private readonly StartupActionsContext _context;

        public StartupActionsHealthCheck(StartupActionsContext context) {
            _context = context;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
            if (_context.IsStartupComplete && _context.Result.Success)
                return Task.FromResult(HealthCheckResult.Healthy("All startup actions completed"));
            
            if (_context.IsStartupComplete && !_context.Result.Success)
                return Task.FromResult(HealthCheckResult.Unhealthy($"Startup action \"{_context.Result.FailedActionName}\" failed to complete: {_context.Result.ErrorMessage}"));
            
            return Task.FromResult(HealthCheckResult.Unhealthy("Startup actions have not completed"));
        }
    }
}