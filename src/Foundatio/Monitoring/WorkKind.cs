namespace Foundatio.Monitoring;

/// <summary>What kind of work an <c>IWorkMonitor</c> entry describes.</summary>
public enum WorkKind
{
    /// <summary>A message delivered to a handler registered with <c>AddConsumer</c> or <c>AddSubscriber</c>.</summary>
    Message,

    /// <summary>A run of a durable job registered with <c>AddJob</c>.</summary>
    Job
}
