using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Utility;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>
/// Turns processing statuses into a health result. Errors that are being retried are Degraded; only a stopped loop, or
/// one that has failed continuously for longer than the threshold, reports the registration's failure status.
/// </summary>
internal static class ProcessingHealth
{
    public static HealthCheckResult Evaluate(IReadOnlyList<ProcessingStatus> statuses, string noneDescription, DateTimeOffset now, TimeSpan unhealthyAfter, HealthStatus failureStatus)
    {
        if (statuses.Count == 0)
            return HealthCheckResult.Healthy(noneDescription);

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var problems = new List<string>();
        var health = HealthStatus.Healthy;

        foreach (var status in statuses)
        {
            var (componentHealth, problem) = Evaluate(status, now, unhealthyAfter, failureStatus);
            if (componentHealth < health)
                health = componentHealth;
            if (problem is not null)
                problems.Add($"{status.Name}: {problem}");

            data[UniqueKey(data, status.Name)] = ToData(status, componentHealth);
        }

        string description = problems.Count > 0
            ? String.Join("; ", problems)
            : String.Join("; ", statuses.GroupBy(s => s.State).Select(g => $"{g.Key}: {String.Join(", ", g.Select(s => s.Name))}"));

        return new HealthCheckResult(health, description, data: data);
    }

    private static (HealthStatus Health, string? Problem) Evaluate(ProcessingStatus status, DateTimeOffset now, TimeSpan unhealthyAfter, HealthStatus failureStatus)
    {
        if (status.State == ProcessingState.Faulted)
            return (failureStatus, $"stopped ({status.LastError})");

        if (status.FailingSince is not { } since)
            return (HealthStatus.Healthy, null);

        var failingFor = now - since;
        string duration = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(failingFor.TotalSeconds))).ToString("c");
        return failingFor >= unhealthyAfter
            ? (failureStatus, $"failing for {duration} ({status.LastError})")
            : (HealthStatus.Degraded, $"retrying after errors for {duration} ({status.LastError})");
    }

    private static Dictionary<string, object> ToData(ProcessingStatus status, HealthStatus health)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = health.ToString(),
            ["state"] = status.State.ToString(),
            ["active"] = status.Active
        };

        if (status.ActiveByName.Count > 0)
            data["activeByName"] = status.ActiveByName;
        if (status.FailingSince is { } failingSince)
            data["failingSince"] = failingSince;
        if (status.LastError is { } lastError)
            data["lastError"] = lastError;
        if (status.LastErrorAt is { } lastErrorAt)
            data["lastErrorAt"] = lastErrorAt;

        return data;
    }

    private static string UniqueKey(Dictionary<string, object> data, string name)
    {
        if (!data.ContainsKey(name))
            return name;

        int suffix = 2;
        while (data.ContainsKey($"{name} ({suffix})"))
            suffix++;

        return $"{name} ({suffix})";
    }
}
