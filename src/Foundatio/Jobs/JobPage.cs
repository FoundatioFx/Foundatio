using System.Collections.Generic;

namespace Foundatio.Jobs;

/// <summary>A page of jobs. Pass <see cref="ContinuationToken"/> to <see cref="JobQuery"/> for the next page.</summary>
public sealed record JobPage(IReadOnlyList<JobInfo> Jobs, string? ContinuationToken);
