using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.HostingSample {
    public class MyCriticalHealthCheck : IHealthCheck {
        private static DateTime _startTime = DateTime.Now;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken()) {
            return DateTime.Now.Subtract(_startTime) > TimeSpan.FromSeconds(10) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Critical resource not available.");
        }
    }
}
