using System;
using System.Collections.Generic;

namespace Foundatio.Jobs;

/// <summary>Options for <see cref="IJobClient.EnqueueAsync{TArgs}(TArgs, EnqueueOptions, System.Threading.CancellationToken)"/>.</summary>
public sealed record EnqueueOptions
{
    /// <summary>
    /// A caller-chosen id. Enqueuing again with the same id returns the existing job instead of creating another,
    /// for as long as the job is retained.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Runs at most one job of this name with this key at a time, across every worker: a job whose key is held by a
    /// running one stays queued, without using an attempt, until that one finishes, fails, is retried or released, or
    /// is deleted. Jobs with other keys, and jobs of other names, are not held up. For example, use a project's id so
    /// two resets of one project never overlap. A job whose lease runs out keeps its key until it is claimed again.
    /// </summary>
    public string? ConcurrencyKey { get; init; }

    /// <summary>
    /// While a job of this name with this key is waiting for its first run, enqueuing another returns the waiting job's
    /// handle instead of creating one. Once the job starts (or is cancelled or deleted), the next enqueue creates a new
    /// job, so work that arrives while a job runs is not lost. Unlike <see cref="JobId"/>, which keeps an id taken for as
    /// long as the job is retained, the key is free again as soon as the job starts.
    /// </summary>
    public string? DeduplicationKey { get; init; }

    /// <summary>Run after this delay.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Run at this time. Set this or <see cref="Delay"/>, not both.</summary>
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>
    /// Links the job to what enqueued it. Defaults to the correlation id of the message or job being handled, then to
    /// the id of <see cref="System.Diagnostics.Activity.Current"/>.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Stored with the job and visible to it as <see cref="IHandlerContext.Headers"/>. When an activity is current, its W3C
    /// trace context is added as <c>traceparent</c> and <c>tracestate</c> unless you set <c>traceparent</c> yourself, so
    /// the job's activity continues the caller's trace.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
