using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>
/// Fails host startup when a Foundatio health check was added for messaging or jobs that are not configured, instead
/// of the check reporting a failure on every probe.
/// </summary>
internal sealed class HealthCheckRequirementsValidator : IHostedService
{
    private readonly IEnumerable<HealthCheckRequirement> _requirements;
    private readonly IServiceProviderIsService? _isService;

    public HealthCheckRequirementsValidator(IEnumerable<HealthCheckRequirement> requirements, IServiceProvider services)
    {
        _requirements = requirements;
        _isService = services.GetService<IServiceProviderIsService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isService is null)
            return Task.CompletedTask;

        var missing = _requirements.Where(r => !_isService.IsService(r.RequiredService)).ToList();
        if (missing.Count == 0)
            return Task.CompletedTask;

        throw new InvalidOperationException(String.Join(" ", missing.Select(r =>
            $"The \"{r.CheckName}\" health check reports on {r.RegisterWith}, which is not configured: call services.{r.RegisterWith}(...), or remove the check.")));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
