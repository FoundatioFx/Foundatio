using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>Versioned schedule storage for tests and single-process applications.</summary>
public sealed class InMemoryScheduledJobStore : IScheduledJobStore
{
    private sealed record Entry(ScheduledJobDefinition Definition, string? Configuration);
    private readonly Dictionary<string, Entry> _definitions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _definitions.TryGetValue(definition.Name, out var current);
            if (definition.Revision != (current?.Definition.Revision ?? 0))
                throw new JobException($"Schedule {definition.Name} changed. Reload it before saving.");
            _definitions[definition.Name] = new Entry(Snapshot(definition with
            {
                Revision = definition.Revision + 1,
                ConfigurationVersion = current?.Definition.ConfigurationVersion ?? 0
            }), current?.Configuration);
        }
        return Task.CompletedTask;
    }

    public Task ReconcileAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.ConfigurationVersion, 1);
        cancellationToken.ThrowIfCancellationRequested();
        string configuration = JsonSerializer.Serialize(definition with { Revision = 0 });
        lock (_lock)
        {
            _definitions.TryGetValue(definition.Name, out var current);
            if (current is not null)
            {
                if (definition.ConfigurationVersion < current.Definition.ConfigurationVersion)
                    return Task.CompletedTask;
                if (definition.ConfigurationVersion == current.Definition.ConfigurationVersion)
                {
                    if (configuration != current.Configuration)
                        throw new JobException($"Declared schedule {definition.Name} changed. Increase ConfigurationVersion to apply it.");
                    return Task.CompletedTask;
                }
            }
            _definitions[definition.Name] = new Entry(Snapshot(definition with { Revision = (current?.Definition.Revision ?? 0) + 1 }), configuration);
        }
        return Task.CompletedTask;
    }

    public Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
            return Task.FromResult(_definitions.TryGetValue(name, out var entry) ? Snapshot(entry.Definition) : null);
    }

    public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
            _definitions.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ScheduleQuery();
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ScheduledJobDefinition>>(_definitions.Values.Where(e => query.AfterName is null || StringComparer.Ordinal.Compare(e.Definition.Name, query.AfterName) > 0).OrderBy(e => e.Definition.Name, StringComparer.Ordinal).Take(query.Limit).Select(e => Snapshot(e.Definition)).ToArray());
    }

    private static ScheduledJobDefinition Snapshot(ScheduledJobDefinition definition)
        => definition with { Payload = definition.Payload is { } payload ? (ReadOnlyMemory<byte>?)payload.ToArray() : null };
}
