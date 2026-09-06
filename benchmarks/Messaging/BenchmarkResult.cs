namespace Foundatio.Messaging.Benchmarks;

public sealed record BenchmarkResult
{
    public required BenchmarkOptions Options { get; init; }
    public required string ResourcePrefix { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public PhaseResult? Measurement { get; init; }
}

public sealed record PhaseResult
{
    public string? Error { get; init; }
    public string? FirstInvalid { get; init; }
    public long Inputs { get; init; }
    public long Deliveries { get; init; }
    public long Duplicates { get; init; }
    public long Invalid { get; init; }
    public long Missing { get; init; }
    public bool HitTrackingLimit { get; init; }
    public double PublishSeconds { get; init; }
    public double TotalSeconds { get; init; }
    public double InputsPerSecond => Inputs / TotalSeconds;
    public double DeliveriesPerSecond => Deliveries / TotalSeconds;
    public long AllocatedBytes { get; init; }
    public double AllocatedBytesPerInput => Inputs > 0 ? AllocatedBytes / (double)Inputs : 0;
    public double CpuMilliseconds { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public int[] Collections { get; init; } = [];
    public double GcPauseMilliseconds { get; init; }
    public required LatencySummary DeliveryLatency { get; init; }
    public required LatencySummary SendCallLatency { get; init; }
    public IReadOnlyList<ProgressSample> Samples { get; init; } = [];
}

public sealed record ProgressSample(double Seconds, long Inputs, long Deliveries, long Outstanding, long WorkingSetBytes, long AllocatedBytes);
