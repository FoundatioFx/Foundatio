using System.Collections.Generic;

namespace Foundatio.Jobs;

/// <summary>A page of jobs, newest first. Pass <see cref="ContinuationToken"/> to <see cref="JobQuery"/> for the next page.</summary>
/// <typeparam name="TJob"><see cref="JobDetails"/> from <see cref="IJobClient"/>, or the stored <see cref="Store.JobInfo"/> from an <see cref="Store.IJobStore"/>.</typeparam>
/// <param name="Jobs">The jobs on this page.</param>
/// <param name="ContinuationToken">Null on the last page.</param>
public sealed record JobPage<TJob>(IReadOnlyList<TJob> Jobs, string? ContinuationToken);
