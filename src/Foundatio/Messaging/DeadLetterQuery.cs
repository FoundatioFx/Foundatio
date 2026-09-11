using System;

namespace Foundatio.Messaging;

/// <summary>A bounded page of dead letters. Pass the last entry ID as AfterId to continue.</summary>
public sealed record DeadLetterQuery
{
    public string? AfterId { get; init; }
    public int Limit { get; init; } = 100;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, 1000);
    }
}
