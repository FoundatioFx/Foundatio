using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Foundatio.Monitoring;

/// <summary>
/// What one instance writes to the cache. Stored as JSON text, so any cache serializer works. Fields may be added
/// without changing <see cref="Version"/>; readers skip snapshots of a version they do not know.
/// </summary>
internal sealed class InstanceSnapshot
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; init; } = CurrentVersion;

    public required string InstanceId { get; init; }
    public required string ServiceName { get; init; }
    public required string MachineName { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The sources the instance receives from, as <see cref="Messaging.Transport.MessageSource.ToString"/> writes them.</summary>
    public IReadOnlyList<string> MessageSources { get; init; } = [];

    public IReadOnlyList<string> JobNames { get; init; } = [];
    public IReadOnlyList<ActiveWorkEntry> Active { get; init; } = [];
    public IReadOnlyList<CompletedWorkEntry> Recent { get; init; } = [];
    public IReadOnlyList<WorkStatsEntry> Stats { get; init; } = [];
    public TimeSpan StatsWindow { get; init; }
}
