using System.Collections.Generic;

namespace Foundatio.Testing;

/// <summary>What one <see cref="FoundatioTestHarness.ProcessPendingAsync(System.TimeSpan?, System.Threading.CancellationToken)"/> call ran.</summary>
public sealed record ProcessedWork
{
    /// <summary>The deliveries handled during the call, with how each ended, including retried attempts.</summary>
    public required IReadOnlyList<RecordedMessage> HandledMessages { get; init; }

    /// <summary>The job runs that finished during the call, including runs that will be retried.</summary>
    public required IReadOnlyList<RecordedJob> JobRuns { get; init; }
}
