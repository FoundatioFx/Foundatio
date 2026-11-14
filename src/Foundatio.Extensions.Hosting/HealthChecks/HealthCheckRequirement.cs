using System;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>A Foundatio health check that was registered, and the service it reports on.</summary>
internal sealed record HealthCheckRequirement(string CheckName, Type RequiredService, string RegisterWith);
