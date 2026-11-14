using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>Reports the message processors of this process, one data entry per source.</summary>
internal sealed class MessagingHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _unhealthyAfter;

    public MessagingHealthCheck(IServiceProvider services, TimeSpan unhealthyAfter)
    {
        _services = services;
        _unhealthyAfter = unhealthyAfter;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_services.GetService<MessagingStatus>() is not { } status)
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "Messaging is not configured: call AddFoundatioMessaging."));

        var now = (_services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        return Task.FromResult(ProcessingHealth.Evaluate(status.GetProcessors(), "No message handlers are registered", now, _unhealthyAfter, context.Registration.FailureStatus));
    }
}
