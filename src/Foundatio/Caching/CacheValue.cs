using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Caching;

public class CacheValue<T>
{
    public CacheValue([AllowNull] T value, bool hasValue)
    {
        // null! is intentional: Value is typed as non-nullable T, but callers must check
        // HasValue or IsNull before accessing it. This follows the TryGet pattern where
        // the value is only meaningful when HasValue is true.
        Value = value!;
        HasValue = hasValue;
    }

    public bool HasValue { get; }

    public bool IsNull => Value is null;

    public T Value { get; }

    public static CacheValue<T> Null { get; } = new(default, true);

    public static CacheValue<T> NoValue { get; } = new(default, false);

    public override string ToString()
    {
        return Value?.ToString() ?? "<null>";
    }
}
