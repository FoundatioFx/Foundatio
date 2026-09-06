using System.Collections;
using System.Collections.Generic;

namespace Foundatio.Jobs;

/// <summary>A bounded page ordered by job ID. Continue until ContinuationToken is null, including after an empty filtered page.</summary>
public sealed class JobPage(IReadOnlyList<JobState> items, string? continuationToken) : IReadOnlyList<JobState>
{
    public string? ContinuationToken { get; } = continuationToken;
    public int Count => items.Count;
    public JobState this[int index] => items[index];
    public IEnumerator<JobState> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
