using System;
using System.Collections.Generic;

namespace Foundatio.Jobs;

/// <summary>Eligibility and ownership for one atomic job claim.</summary>
public sealed record JobClaimRequest
{
    /// <summary>Diagnostic worker identity. Ownership is fenced by a fresh claim token for every run.</summary>
    public required string NodeId { get; init; }

    /// <summary>Registered wire names this worker can execute.</summary>
    public required IReadOnlyCollection<string> JobTypes { get; init; }

    /// <summary>Renewable execution lease. Default five minutes.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>How an owned execution ended.</summary>
public enum JobCompletionKind
{
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

/// <summary>Completion input for an atomic, claim-guarded job transition.</summary>
public sealed record JobCompletion
{
    public required JobCompletionKind Kind { get; init; }
    public string? Error { get; init; }
}
