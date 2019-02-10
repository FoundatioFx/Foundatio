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
            if (_context.IsStartupComplete)
                return Task.FromResult(HealthCheckResult.Healthy("All startup tasks complete"));

            return Task.FromResult(HealthCheckResult.Unhealthy("Startup tasks not complete"));
        }
    }
}