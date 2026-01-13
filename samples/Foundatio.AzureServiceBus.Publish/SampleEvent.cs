using System;

namespace Foundatio.AzureServiceBus.Samples;

public record SampleEvent
{
    public string EventType { get; init; } = String.Empty;
    public string Data { get; init; } = String.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = String.Empty;
}
