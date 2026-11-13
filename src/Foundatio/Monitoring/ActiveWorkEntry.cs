using System;

namespace Foundatio.Monitoring;

/// <summary>A message or job being handled, as an instance snapshot stores it.</summary>
internal sealed class ActiveWorkEntry
{
    public WorkKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Source { get; init; }
    public string? Handler { get; init; }
    public int Attempt { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public DateTimeOffset? ProgressAt { get; init; }
}
