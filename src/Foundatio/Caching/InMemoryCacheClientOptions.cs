using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching;

public class InMemoryCacheClientOptions : SharedOptions
{
    /// <summary>
    /// The maximum number of items to store in the cache.
    /// </summary>
    public int? MaxItems { get; set; } = 10000;

    /// <summary>
    /// The maximum memory size in bytes that the cache can consume. If null, no memory limit is applied.
    /// </summary>
    public long? MaxMemorySize { get; set; }

    /// <summary>
    /// Function to calculate the size of cache entries in bytes. Required when <see cref="MaxMemorySize"/> or <see cref="MaxEntrySize"/> is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entry sizing is opt-in for performance. When <see cref="MaxMemorySize"/> or <see cref="MaxEntrySize"/> is set, 
    /// a <see cref="SizeCalculator"/> must also be provided.
    /// </para>
    /// <para>
    /// Use <see cref="InMemoryCacheClientOptionsBuilder.WithDynamicSizing(long, ILoggerFactory)"/> for automatic size calculation using <see cref="Foundatio.Utility.SizeCalculator"/>,
    /// or <see cref="InMemoryCacheClientOptionsBuilder.WithFixedSizing"/> for maximum performance with uniform entry sizes.
    /// </para>
    /// <para>
    /// <strong>Custom SizeCalculator contract:</strong>
    /// <list type="bullet">
    /// <item>Must return a non-negative value representing the estimated size in bytes</item>
    /// <item>Negative return values will cause the entry to be skipped (not cached) with a warning logged</item>
    /// <item>Should be thread-safe as it may be called concurrently</item>
    /// <item>Should handle null values gracefully (typically return 8 bytes for a null reference)</item>
    /// </list>
    /// </para>
    /// </remarks>
    public Func<object, long> SizeCalculator { get; set; }

    /// <summary>
    /// The maximum size in bytes for individual cache entries. Entries larger than this will be skipped (not cached) and a warning will be logged.
    /// If null, no entry size limit is enforced. Must be less than or equal to <see cref="MaxMemorySize"/> when both are set.
    /// </summary>
    public long? MaxEntrySize { get; set; }

    /// <summary>
    /// When true, throws a <see cref="MaxEntrySizeExceededCacheException"/> if an entry exceeds <see cref="MaxEntrySize"/>.
    /// When false (default), oversized entries are skipped and a warning is logged.
    /// </summary>
    public bool ShouldThrowOnMaxEntrySizeExceeded { get; set; } = false;

    /// <summary>
    /// Whether or not values should be cloned during get and set to make sure that any cache entry changes are isolated.
    /// </summary>
    public bool CloneValues { get; set; } = false;

    /// <summary>
    /// Whether or not an error when deserializing a cache value should result in an exception being thrown or if it should just return an empty cache value.
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
    /// Sets the maximum memory size in bytes. Use this with <see cref="SizeCalculator"/> for custom size calculation.
    /// For most scenarios, prefer <see cref="WithDynamicSizing(long, ILoggerFactory)"/> or <see cref="WithFixedSizing"/> which set both the limit and calculator.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    public InMemoryCacheClientOptionsBuilder MaxMemorySize(long maxMemorySize)
    {
        Target.MaxMemorySize = maxMemorySize;
        return this;
    }

    /// <summary>
    /// Sets a custom function to calculate the size of cache entries in bytes.
    /// Must be used with <see cref="MaxMemorySize"/> to enable memory-based eviction.
    /// </summary>
    /// <param name="sizeCalculator">Function that returns the size of an entry in bytes.</param>
    public InMemoryCacheClientOptionsBuilder SizeCalculator(Func<object, long> sizeCalculator)
    {
        Target.SizeCalculator = sizeCalculator;
        return this;
    }

    /// <summary>
    /// Use a fixed size for all cache entries with a memory limit. This provides maximum performance by bypassing size calculation entirely.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    /// <param name="averageEntrySize">The fixed size in bytes to use for each cache entry.</param>
    public InMemoryCacheClientOptionsBuilder WithFixedSizing(long maxMemorySize, long averageEntrySize)
    {
        Target.MaxMemorySize = maxMemorySize;
        Target.SizeCalculator = _ => averageEntrySize;
        return this;
    }

    /// <summary>
    /// Use the SizeCalculator to calculate entry sizes dynamically with a memory limit.
    /// The SizeCalculator uses fast paths for common types and falls back to JSON serialization for complex objects.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public InMemoryCacheClientOptionsBuilder WithDynamicSizing(long maxMemorySize, ILoggerFactory loggerFactory = null)
    {
        Target.MaxMemorySize = maxMemorySize;
        var sizeCalculator = new Utility.SizeCalculator(loggerFactory);
        Target.SizeCalculator = sizeCalculator.CalculateSize;
        return this;
    }

    /// <summary>
    /// Use the SizeCalculator to calculate entry sizes dynamically with a memory limit.
    /// The SizeCalculator uses fast paths for common types and falls back to JSON serialization for complex objects.
    /// </summary>
    /// <param name="maxMemorySize">The maximum memory size in bytes that the cache can consume.</param>
    /// <param name="maxTypeCacheSize">Maximum number of types to cache size calculations for.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public InMemoryCacheClientOptionsBuilder WithDynamicSizing(long maxMemorySize, int maxTypeCacheSize, ILoggerFactory loggerFactory = null)
    {
        Target.MaxMemorySize = maxMemorySize;
        var sizeCalculator = new Utility.SizeCalculator(maxTypeCacheSize, loggerFactory);
        Target.SizeCalculator = sizeCalculator.CalculateSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum size in bytes for individual cache entries. Entries exceeding this size will be skipped (not cached) and a warning will be logged.
    /// Must be less than or equal to <see cref="MaxMemorySize"/> when both are set.
    /// </summary>
    /// <param name="maxEntrySize">The maximum entry size in bytes. Must be positive when specified.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when maxEntrySize is less than or equal to zero.</exception>
    public InMemoryCacheClientOptionsBuilder MaxEntrySize(long? maxEntrySize)
    {
        if (maxEntrySize.HasValue && maxEntrySize.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntrySize), "MaxEntrySize must be positive when specified.");

        Target.MaxEntrySize = maxEntrySize;
        return this;
    }

    /// <summary>
    /// When called, causes the cache to throw a <see cref="MaxEntrySizeExceededCacheException"/> when an entry exceeds <see cref="MaxEntrySize"/>.
    /// By default, oversized entries are skipped and a warning is logged.
    /// </summary>
    /// <param name="shouldThrow">Whether to throw on oversized entries. Defaults to true when this method is called.</param>
    public InMemoryCacheClientOptionsBuilder ShouldThrowOnMaxEntrySizeExceeded(bool shouldThrow = true)
    {
        Target.ShouldThrowOnMaxEntrySizeExceeded = shouldThrow;
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
