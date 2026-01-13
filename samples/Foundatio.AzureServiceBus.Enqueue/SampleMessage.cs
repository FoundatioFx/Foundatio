using System;

namespace Foundatio.AzureServiceBus.Samples;

public record SampleMessage
{
    public string Message { get; init; } = String.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = String.Empty;
}
