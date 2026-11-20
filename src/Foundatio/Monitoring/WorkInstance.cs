using System;
using System.Collections.Generic;
using Foundatio.Messaging.Transport;

namespace Foundatio.Monitoring;

/// <summary>A process that handles messages or runs jobs, as its latest snapshot describes it.</summary>
public sealed record WorkInstance
{
    /// <summary>Unique for the life of the process: the machine name, process id and a random suffix.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The service the instance belongs to: <see cref="MonitoringOptions.ServiceName"/>, the messaging service name, or the application name.</summary>
    public required string ServiceName { get; init; }

    /// <summary>The machine the process runs on.</summary>
    public required string MachineName { get; init; }

    /// <summary>The operating system's id for the process.</summary>
    public int ProcessId { get; init; }

    /// <summary>When monitoring started in the process.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the snapshot was taken, by the instance's clock.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The queues and subscriptions the instance is receiving from.</summary>
    public IReadOnlyList<MessageSource> MessageSources { get; init; } = [];

    /// <summary>The names of the jobs the instance runs.</summary>
    public IReadOnlyList<string> JobNames { get; init; } = [];

    /// <summary>True for the process reading the monitor; its data is live rather than from its last snapshot.</summary>
    public bool IsCurrent { get; init; }
}
