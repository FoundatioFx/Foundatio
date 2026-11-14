namespace Foundatio.Monitoring;

/// <summary>Narrows what <see cref="IWorkMonitor"/> returns. Unset properties match everything.</summary>
public sealed record WorkFilter
{
    /// <summary>Only messages, or only jobs.</summary>
    public WorkKind? Kind { get; init; }

    /// <summary>Only this message type name or job name, compared exactly.</summary>
    public string? Name { get; init; }

    /// <summary>Only work on this instance; see <see cref="WorkInstance.InstanceId"/>.</summary>
    public string? InstanceId { get; init; }
}
