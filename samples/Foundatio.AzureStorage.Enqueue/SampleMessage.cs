using System;

namespace Foundatio.AzureStorage.Samples;

public record SampleMessage
{
    public string Message { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = string.Empty;
}
