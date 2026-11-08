using System;
using System.Collections.Generic;

namespace Foundatio.Jobs.Store;

/// <summary>What a worker can run.</summary>
public sealed record JobClaimRequest
{
    public required IReadOnlyCollection<string> Names { get; init; }
    public required string WorkerId { get; init; }
    public required TimeSpan LeaseDuration { get; init; }
}
