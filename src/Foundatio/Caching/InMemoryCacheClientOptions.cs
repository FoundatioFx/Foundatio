using System;

namespace Foundatio.Caching;

public class InMemoryCacheClientOptions : SharedOptions
{
    /// <summary>
    /// The maximum number of items to store in the cache
    /// </summary>
    public int? MaxItems { get; set; } = 10000;

    /// <summary>
    /// The maximum memory size in bytes that the cache can consume. If null, no memory limit is applied.
    /// </summary>
    public long? MaxMemorySize { get; set; }

    /// <summary>
    /// Function to calculate the size of cache objects in bytes. If null, uses the default ObjectSizer.
    /// </summary>
    public Func<object, long> ObjectSizeCalculator { get; set; } = ObjectSizer.Default;

    /// <summary>
    /// The maximum size in bytes for individual cache objects. Objects larger than this will trigger a warning log. If null, no size warnings are logged.
    /// </summary>
    public long? MaxObjectSize { get; set; }

    /// <summary>
    /// Whether or not values should be cloned during get and set to make sure that any cache entry changes are isolated
    /// </summary>
    public bool CloneValues { get; set; } = false;

    /// <summary>
    /// Whether or not an error when deserializing a cache value should result in an exception being thrown or if it should just return an empty cache value
    /// </summary>
    public bool ShouldThrowOnSerializationError { get; set; } = true;
}

public class InMemoryCacheClientOptionsBuilder : SharedOptionsBuilder<InMemoryCacheClientOptions, InMemoryCacheClientOptionsBuilder>
{
    public InMemoryCacheClientOptionsBuilder MaxItems(int? maxItems)
    {
        Target.MaxItems = maxItems;
        return this;
    }

    public InMemoryCacheClientOptionsBuilder MaxMemorySize(long? maxMemorySize)
    {
        Target.MaxMemorySize = maxMemorySize;
        return this;
    }

    public InMemoryCacheClientOptionsBuilder ObjectSizeCalculator(Func<object, long> sizeCalculator)
    {
        Target.ObjectSizeCalculator = sizeCalculator ?? ObjectSizer.Default;
        return this;
    }

    public InMemoryCacheClientOptionsBuilder MaxObjectSize(long? maxObjectSize)
    {
        Target.MaxObjectSize = maxObjectSize;
        return this;
    }

    public InMemoryCacheClientOptionsBuilder CloneValues(bool cloneValues)
    {
        Target.CloneValues = cloneValues;
        return this;
    }

    public InMemoryCacheClientOptionsBuilder ShouldThrowOnSerializationError(bool shouldThrow)
    {
        Target.ShouldThrowOnSerializationError = shouldThrow;
        return this;
    }
}
