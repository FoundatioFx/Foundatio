using System;

namespace Foundatio.Jobs;

/// <summary>A bounded page of schedules ordered by name. Use the last name as AfterName for the next page.</summary>
public sealed record ScheduleQuery
{
    public string? AfterName { get; init; }
    public int Limit { get; init; } = 100;

    /// <summary>Checks the page size before querying storage.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, 1000);
    }
}
