using System;

namespace Foundatio.Jobs;

/// <summary>
/// Base exception for job-runtime errors (unresolvable job types, untriggerable schedules). Derives from
/// <see cref="InvalidOperationException"/> so catch blocks written against the general type keep working.
/// </summary>
public class JobException : InvalidOperationException
{
    public JobException() { }

    public JobException(string message) : base(message) { }

    public JobException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when a scheduled-job operation addresses a schedule name that is not registered.</summary>
public sealed class ScheduledJobNotFoundException : JobException
{
    public ScheduledJobNotFoundException(string name) : base($"No scheduled job named \"{name}\" is registered.")
    {
        Name = name;
    }

    /// <summary>The schedule name that could not be found.</summary>
    public string Name { get; }
}

/// <summary>Thrown when a scheduled job is triggered while its schedule is disabled.</summary>
public sealed class ScheduledJobDisabledException : JobException
{
    public ScheduledJobDisabledException(string name) : base($"Scheduled job \"{name}\" is disabled. Enable it before triggering (SetEnabledAsync(\"{name}\", true)).")
    {
        Name = name;
    }

    /// <summary>The name of the disabled schedule.</summary>
    public string Name { get; }
}
