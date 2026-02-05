using System;

namespace Foundatio.Utility;

/// <summary>
/// Indicates that a type exposes a time provider for time-related operations.
/// Enables testability by allowing time to be controlled in tests.
/// </summary>
public interface IHaveTimeProvider
{
    /// <summary>
    /// Gets the time provider used for time-related operations.
    /// </summary>
    TimeProvider TimeProvider { get; }
}

public static class TimeProviderExtensions
{
    public static TimeProvider GetTimeProvider(this object target)
    {
        return target is IHaveTimeProvider accessor ? accessor.TimeProvider ?? TimeProvider.System : TimeProvider.System;
    }
}
