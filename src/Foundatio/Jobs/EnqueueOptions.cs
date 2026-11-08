using System;

namespace Foundatio.Jobs;

/// <summary>Options for <see cref="IJobClient.EnqueueAsync{TArgs}(TArgs, EnqueueOptions, System.Threading.CancellationToken)"/>.</summary>
public sealed record EnqueueOptions
{
    /// <summary>
    /// A caller-chosen id. Enqueuing again with the same id returns the existing job instead of creating another,
    /// for as long as the job is retained.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>Run after this delay.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Run at this time. Takes precedence over <see cref="Delay"/>.</summary>
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>Defaults to the correlation id of the message or job being handled, if any.</summary>
    public string? CorrelationId { get; init; }
}
