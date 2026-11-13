using System;

namespace Foundatio.AzureServiceBus.Samples;

public record SampleMessage
{
    public required string Message { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required string Source { get; init; }
}
