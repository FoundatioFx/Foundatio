using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Jobs.Store;

/// <summary>What a worker can run.</summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed record JobClaimRequest
{
    public required IReadOnlyCollection<string> Names { get; init; }
    public required string WorkerId { get; init; }
    public required TimeSpan LeaseDuration { get; init; }
}
