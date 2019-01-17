using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Exceptionless.Web {
    public class SampleHealthCheck : IHealthCheck {
        public string Name => "sample_check";

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(HealthCheckResult.Healthy("The startup task is finished."));
        }
    }
}