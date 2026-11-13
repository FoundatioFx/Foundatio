using System;

namespace Foundatio.Monitoring;

/// <summary>A finished message delivery or job run, as an instance snapshot stores it.</summary>
internal sealed class CompletedWorkEntry
{
    public WorkKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Source { get; init; }
    public string? Handler { get; init; }
    public int Attempt { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public WorkOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
}
