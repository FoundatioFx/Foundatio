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

    /// <summary>Run after this delay.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Run at this time. Takes precedence over <see cref="Delay"/>.</summary>
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
