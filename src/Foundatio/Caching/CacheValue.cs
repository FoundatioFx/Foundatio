namespace Foundatio.Caching
{
    public class CacheValue<T>
    {
        public CacheValue(T value, bool hasValue)
        {
            Value = value;
            HasValue = hasValue;
        }

        public bool HasValue { get; }

        public bool IsNull => Value == null;

        public T Value { get; }

        public static CacheValue<T> Null { get; } = new CacheValue<T>(default, true);

        public static CacheValue<T> NoValue { get; } = new CacheValue<T>(default, false);

        public override string ToString()
        {
            return Value?.ToString() ?? "<null>";
        }
    }
}
