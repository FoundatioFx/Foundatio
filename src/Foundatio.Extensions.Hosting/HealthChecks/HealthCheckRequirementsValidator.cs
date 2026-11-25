using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>
/// Fails host startup when a Foundatio health check was added for messaging or jobs that are not configured, instead
/// of the check reporting a failure on every probe. It checks in <see cref="StartingAsync"/>, before any hosted service
/// starts, so a misconfigured process never opens a receiver or claims a job.
/// </summary>
internal sealed class HealthCheckRequirementsValidator : IHostedLifecycleService
{
    private readonly IEnumerable<HealthCheckRequirement> _requirements;
    private readonly IServiceProvider _services;
    private readonly IServiceProviderIsService? _isService;

    public HealthCheckRequirementsValidator(IEnumerable<HealthCheckRequirement> requirements, IServiceProvider services)
    {
        _requirements = requirements;
        _services = services;
        _isService = services.GetService<IServiceProviderIsService>();
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_isService is null)
            return Task.CompletedTask;

        var problems = new List<string>();
        foreach (var requirement in _requirements)
        {
            if (!_isService.IsService(requirement.RequiredService))
            {
                problems.Add($"The \"{requirement.CheckName}\" health check reports on {requirement.RegisterWith}, which is not configured: call services.{requirement.RegisterWith}(...), or remove the check.");
                continue;
            }

            try
            {
                requirement.Validate?.Invoke(_services);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                problems.Add($"The \"{requirement.CheckName}\" health check can't report correctly: {ex.Message}");
            }
        }

        if (problems.Count == 0)
            return Task.CompletedTask;

        throw new InvalidOperationException(String.Join(" ", problems));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
