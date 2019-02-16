using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Hosting.Startup {
    public class StartupHealthCheck : IHealthCheck {
        private readonly StartupContext _context;

        public StartupHealthCheck(StartupContext context) {
            _context = context;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
            return Task.FromResult(_context.IsStartupComplete ? HealthCheckResult.Healthy("All startup actions completed") : HealthCheckResult.Unhealthy("Startup actions have not completed"));
        }
    }
}