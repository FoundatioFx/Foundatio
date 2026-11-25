using System;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>
/// A Foundatio health check that was registered, the service it reports on, and what else must hold for it to report
/// correctly: <see cref="Validate"/> throws <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/>
/// explaining what doesn't.
/// </summary>
internal sealed record HealthCheckRequirement(string CheckName, Type RequiredService, string RegisterWith, Action<IServiceProvider>? Validate = null);
