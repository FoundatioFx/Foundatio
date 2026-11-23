using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Jobs.Store;

/// <summary>What a worker can run.</summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed record JobClaimRequest
{
    /// <summary>The job names the worker runs. Only jobs with one of these names are claimed.</summary>
    public required IReadOnlyCollection<string> Names { get; init; }
    /// <summary>Identifies the worker; stored as <see cref="JobInfo.WorkerId"/> while it holds the job.</summary>
    public required string WorkerId { get; init; }
    /// <summary>How long the claim lasts unless renewed. Afterwards another worker may claim the job.</summary>
    public required TimeSpan LeaseDuration { get; init; }

    /// <summary>
    /// The most jobs of a name one <see cref="IJobStore.ClaimBatchAsync"/> may take, for names whose job type limits how
    /// many of it run at once; names not in it are limited only by the batch's count. Null when no name is limited.
    /// A single <see cref="IJobStore.ClaimAsync"/> can ignore it: a worker only requests names it has room for.
    /// </summary>
    public IReadOnlyDictionary<string, int>? MaxPerName { get; init; }
}
