using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Caching;

public class CacheValue<T>
{
    public CacheValue([AllowNull] T value, bool hasValue)
    {
        Value = value;
        HasValue = hasValue;
    }

    public bool HasValue { get; }

    public bool IsNull => Value is null;

    [MaybeNull]
    public T Value { get; }

    public static CacheValue<T> Null { get; } = new(default, true);

    public static CacheValue<T> NoValue { get; } = new(default, false);

    public override string ToString()
    {
        return Value?.ToString() ?? "<null>";
    }
}
