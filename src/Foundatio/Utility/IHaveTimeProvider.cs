using System;

namespace Foundatio.Utility;

public interface IHaveTimeProvider
{
    TimeProvider TimeProvider { get; }
}

public static class TimeProviderExtensions
{
    public static TimeProvider GetTimeProvider(this object target)
    {
        return target is IHaveTimeProvider accessor ? accessor.TimeProvider ?? TimeProvider.System : TimeProvider.System;
    }
}
