using System;

namespace Foundatio.AzureServiceBus.Samples;

public record SampleEvent
{
    public required string EventType { get; init; }
    public required string Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required string Source { get; init; }
}
