using System;
using Microsoft.Extensions.Logging;

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
    /// Function to calculate the size of cache objects in bytes. If null, no size calculation is performed
    /// unless <see cref="InMemoryCacheClientOptions.MaxMemorySize"/> is set, in which case an <see cref="ObjectSizer"/> is created automatically.
    /// </summary>
    /// <remarks>
    /// Object sizing is opt-in for performance. When <see cref="InMemoryCacheClientOptions.MaxMemorySize"/> is set but no calculator is provided,
    /// an <see cref="ObjectSizer"/> instance is created automatically. The default implementation uses fast paths for common types
    /// and falls back to JSON serialization for complex objects.
    /// For performance-critical scenarios, use <see cref="InMemoryCacheClientOptionsBuilder.UseFixedObjectSize"/> to bypass calculation.
    /// </remarks>
    public Func<object, long> ObjectSizeCalculator { get; set; }

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

    /// <summary>
    /// Sets the maximum memory size in bytes. Use this with <see cref="ObjectSizeCalculator"/> for custom size calculation.
    /// For most scenarios, prefer UseObjectSizer or <see cref="UseFixedObjectSize"/> which set both the limit and calculator.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    public InMemoryCacheClientOptionsBuilder MaxMemorySize(long maxMemorySize)
    {
        Target.MaxMemorySize = maxMemorySize;
        return this;
    }

    /// <summary>
    /// Sets a custom function to calculate the size of cache objects in bytes.
    /// Must be used with <see cref="MaxMemorySize"/> to enable memory-based eviction.
    /// </summary>
    /// <param name="sizeCalculator">Function that returns the size of an object in bytes.</param>
    public InMemoryCacheClientOptionsBuilder ObjectSizeCalculator(Func<object, long> sizeCalculator)
    {
        Target.ObjectSizeCalculator = sizeCalculator;
        return this;
    }

    /// <summary>
    /// Use a fixed size for all cache objects with a memory limit. This provides maximum performance by bypassing size calculation entirely.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    /// <param name="averageObjectSize">The fixed size in bytes to use for each cache entry.</param>
    public InMemoryCacheClientOptionsBuilder UseFixedObjectSize(long maxMemorySize, long averageObjectSize)
    {
        Target.MaxMemorySize = maxMemorySize;
        Target.ObjectSizeCalculator = _ => averageObjectSize;
        return this;
    }

    /// <summary>
    /// Use the ObjectSizer to calculate object sizes with a memory limit.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public InMemoryCacheClientOptionsBuilder UseObjectSizer(long maxMemorySize, ILoggerFactory loggerFactory = null)
    {
        Target.MaxMemorySize = maxMemorySize;
        var sizer = new ObjectSizer(loggerFactory);
        Target.ObjectSizeCalculator = sizer.CalculateSize;
        return this;
    }

    /// <summary>
    /// Use the ObjectSizer to calculate object sizes with a memory limit and custom type cache size.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    /// <param name="maxTypeCacheSize">Maximum number of types to cache size calculations for. Default is 1000.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public InMemoryCacheClientOptionsBuilder UseObjectSizer(long maxMemorySize, int maxTypeCacheSize = 1000, ILoggerFactory loggerFactory = null)
    {
        Target.MaxMemorySize = maxMemorySize;
        var sizer = new ObjectSizer(maxTypeCacheSize, loggerFactory);
        Target.ObjectSizeCalculator = sizer.CalculateSize;
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
