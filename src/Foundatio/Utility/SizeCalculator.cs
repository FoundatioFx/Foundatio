using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

/// <summary>
/// Calculates the estimated memory size of objects. Uses fast paths for common types
/// and falls back to JSON serialization for complex objects.
/// </summary>
/// <remarks>
/// The type size cache is bounded to prevent unbounded memory growth. When the cache
/// reaches its limit, older entries are evicted. Call <see cref="Dispose"/> to clear
/// the cache when the calculator is no longer needed.
///
/// Note: For collections implementing <see cref="ICollection"/>, size estimation samples
/// the first 50 items and extrapolates based on the collection's total item count. For
/// other <see cref="System.Collections.IEnumerable"/> types, only the sampled items are
/// measured without extrapolation. In both cases, estimates may underestimate size if
/// unsampled later items are significantly larger.
/// </remarks>
public class SizeCalculator : IDisposable
{
    private ConcurrentDictionary<Type, TypeSizeCacheEntry> _typeSizeCache;
    private readonly int _maxTypeCacheSize;
    private long _cacheAccessCounter;
    private bool _disposed;
    private readonly object _evictionLock = new();

    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = MaxRecursionDepth
    };

    private readonly ILogger _logger;

    /// <summary>
    /// Default maximum number of types to cache size calculations for.
    /// </summary>
    private const int DefaultMaxTypeCacheSize = 1000;

    /// <summary>
    /// Size of a null reference in bytes.
    /// </summary>
    private const long ReferenceSize = 8;

    /// <summary>
    /// Base overhead for string objects (object header + length field + null terminator padding).
    /// </summary>
    private const long StringOverhead = 24;

    /// <summary>
    /// Base overhead for array objects (object header + length + type pointer).
    /// </summary>
    private const long ArrayOverhead = 24;

    /// <summary>
    /// Base overhead for collection objects (object header + internal array reference + count + version).
    /// </summary>
    private const long CollectionOverhead = 32;

    /// <summary>
    /// Base overhead for complex objects (object header + method table pointer).
    /// </summary>
    private const long ObjectOverhead = 24;

    /// <summary>
    /// Default size estimate when type size cannot be determined.
    /// </summary>
    private const long DefaultObjectSize = 64;

    /// <summary>
    /// Maximum number of collection items to sample for size estimation.
    /// Only the first N items are measured; the average is extrapolated to the full collection.
    /// </summary>
    private const int CollectionSampleLimit = 50;

    /// <summary>
    /// Maximum recursion depth for nested collection size calculation to prevent stack overflow.
    /// </summary>
    private const int MaxRecursionDepth = 64;

    /// <summary>
    /// Maximum percentage of cache entries to evict at once (10%).
    /// </summary>
    private const int EvictionPercentage = 10;

    /// <summary>
    /// Maximum number of entries to evict in a single eviction pass.
    /// </summary>
    private const int MaxEvictionCount = 100;

    /// <summary>
    /// Static lookup table for primitive and common value type sizes.
    /// Uses FrozenDictionary for optimal read performance.
    /// </summary>
    private static readonly FrozenDictionary<Type, long> TypeSizeLookupTable = new Dictionary<Type, long>
    {
        // Primitives (1 byte)
        [typeof(bool)] = 1,
        [typeof(byte)] = 1,
        [typeof(sbyte)] = 1,
        // Primitives (2 bytes)
        [typeof(char)] = 2,
        [typeof(short)] = 2,
        [typeof(ushort)] = 2,
        // Primitives (4 bytes)
        [typeof(int)] = 4,
        [typeof(uint)] = 4,
        [typeof(float)] = 4,
        // Primitives (8 bytes)
        [typeof(long)] = 8,
        [typeof(ulong)] = 8,
        [typeof(double)] = 8,
        [typeof(DateTime)] = 8,
        [typeof(TimeSpan)] = 8,
        [typeof(nint)] = 8,
        [typeof(nuint)] = 8,
        // Larger value types (16 bytes)
        [typeof(decimal)] = 16,
        [typeof(Guid)] = 16,
        [typeof(DateTimeOffset)] = 16,
        // Nullable primitives (underlying size + 1 for hasValue flag, but cached as underlying for simplicity)
        [typeof(bool?)] = 2,
        [typeof(byte?)] = 2,
        [typeof(sbyte?)] = 2,
        [typeof(char?)] = 3,
        [typeof(short?)] = 3,
        [typeof(ushort?)] = 3,
        [typeof(int?)] = 5,
        [typeof(uint?)] = 5,
        [typeof(float?)] = 5,
        [typeof(long?)] = 9,
        [typeof(ulong?)] = 9,
        [typeof(double?)] = 9,
        [typeof(DateTime?)] = 9,
        [typeof(TimeSpan?)] = 9,
        [typeof(decimal?)] = 17,
        [typeof(Guid?)] = 17,
        [typeof(DateTimeOffset?)] = 17,
    }.ToFrozenDictionary();

    /// <summary>
    /// Creates a new SizeCalculator instance with default settings.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public SizeCalculator(ILoggerFactory loggerFactory) : this(DefaultMaxTypeCacheSize, loggerFactory)
    {
    }

    /// <summary>
    /// Creates a new SizeCalculator instance.
    /// </summary>
    /// <param name="maxTypeCacheSize">Maximum number of types to cache size calculations for. Default is 1000.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public SizeCalculator(int maxTypeCacheSize = DefaultMaxTypeCacheSize, ILoggerFactory loggerFactory = null)
    {
        _maxTypeCacheSize = maxTypeCacheSize > 0 ? maxTypeCacheSize : DefaultMaxTypeCacheSize;
        // Pre-size dictionary to avoid resizing; -1 uses default concurrency level
        _typeSizeCache = new ConcurrentDictionary<Type, TypeSizeCacheEntry>(-1, _maxTypeCacheSize);

        _logger = loggerFactory?.CreateLogger<SizeCalculator>() ?? NullLogger<SizeCalculator>.Instance;
    }

    /// <summary>
    /// Gets the current number of types in the size cache.
    /// </summary>
    public int TypeCacheCount => _typeSizeCache?.Count ?? 0;

    /// <summary>
    /// Clears the type size cache and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _typeSizeCache?.Clear();
        _typeSizeCache = null;
    }

    /// <summary>
    /// Calculates the estimated memory size of an object in bytes.
    /// </summary>
    /// <param name="value">The object to calculate size for.</param>
    /// <returns>Estimated size in bytes.</returns>
    /// <remarks>
    /// <para>
    /// For primitive types passed as <c>object</c>, this method returns the raw value size
    /// (e.g., 4 bytes for int, 8 bytes for long) rather than the full boxed object size.
    /// Boxed primitives in .NET actually consume more memory (typically 12-24 bytes due to
    /// object header overhead), but this calculator prioritizes performance over absolute
    /// accuracy for primitive types. The returned sizes are useful for relative comparisons
    /// and capacity planning.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown if the calculator has been disposed.</exception>
    public long CalculateSize(object value)
    {
        return CalculateSizeInternal(value, 0);
    }

    private long CalculateSizeInternal(object value, int depth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (value is null)
            return ReferenceSize;

        // Prevent stack overflow from deeply nested structures
        if (depth >= MaxRecursionDepth)
        {
            _logger.LogWarning("Maximum recursion depth ({MaxDepth}) reached during size calculation", MaxRecursionDepth);
            return DefaultObjectSize;
        }

        // Fast paths for common types - no caching needed
        switch (value)
        {
            case string stringValue:
                return StringOverhead + (long)stringValue.Length * 2; // Object overhead + UTF-16 chars
            case bool:
                return 1;
            case byte:
            case sbyte:
                return 1;
            case char:
            case short:
            case ushort:
                return 2;
            case int:
            case uint:
            case float:
                return 4;
            case long:
            case ulong:
            case double:
            case DateTime:
            case TimeSpan:
                return 8;
            case decimal:
            case Guid:
            case DateTimeOffset: // DateTime (8) + TimeSpan offset (8) = 16 bytes
                return 16;
        }

        var type = value.GetType();

        // Handle nullable types - check static dictionary first for common nullable types
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            if (TypeSizeLookupTable.TryGetValue(type, out long nullableSize))
                return nullableSize;

            var underlyingType = Nullable.GetUnderlyingType(type);
            return GetCachedTypeSize(underlyingType) + 1; // Add 1 for hasValue flag
        }

        // Handle arrays efficiently
        if (value is Array array)
        {
            long size = ArrayOverhead;
            var elementType = array.GetType().GetElementType();

            // For value type arrays (primitives and common structs like DateTime, Guid),
            // we can calculate size efficiently using cached type sizes
            if (array.Length > 0 && elementType is { IsValueType: true })
            {
                long elementSize = GetCachedTypeSize(elementType);
                if (elementSize > 0)
                {
                    // Use long arithmetic and guard against overflow when calculating total element size
                    long elementCount = array.Length;
                    if (elementSize > Int64.MaxValue / elementCount)
                        return Int64.MaxValue;

                    size += elementCount * elementSize;
                    return size;
                }
            }

            // For string arrays, calculate actual string sizes directly
            if (elementType == typeof(string))
            {
                // Account for reference pointer for each array slot
                size += array.Length * ReferenceSize;
                foreach (string stringElement in array)
                {
                    if (stringElement is not null)
                        size += StringOverhead + (long)stringElement.Length * 2;
                }
                return size;
            }
        }

        // Handle collections with sampling for large ones
        if (value is IEnumerable enumerable and not string)
        {
            long size = CollectionOverhead;
            int count = 0;
            long itemSizeSum = 0;

            foreach (object item in enumerable)
            {
                count++;
                itemSizeSum += CalculateSizeInternal(item, depth + 1);
                if (count >= CollectionSampleLimit)
                {
                    // Estimate based on sample
                    if (enumerable is ICollection collection)
                    {
                        long avgItemSize = itemSizeSum / count;
                        return size + collection.Count * avgItemSize + collection.Count * ReferenceSize;
                    }
                    break;
                }
            }

            return size + itemSizeSum + count * ReferenceSize; // Collection overhead + items + reference overhead
        }

        // Fall back to JSON serialization for complex objects
        try
        {
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonSerializerOptions);
            return jsonBytes.Length + ObjectOverhead;
        }
        catch (Exception ex)
        {
            // If JSON serialization fails, fall back to cached type size estimation
            _logger.LogError(ex, "JSON serialization failed for type {TypeName}, falling back to type size estimation", type.Name);
            return GetCachedTypeSize(type);
        }
    }

    private long GetCachedTypeSize(Type type)
    {
        var cache = _typeSizeCache;
        if (cache is null)
            return DefaultObjectSize; // Default if disposed

        long accessOrder = Interlocked.Increment(ref _cacheAccessCounter);

        if (cache.TryGetValue(type, out var entry))
        {
            // Update access time for LRU using thread-safe atomic operation
            Interlocked.Exchange(ref entry.LastAccessField, accessOrder);
            return entry.Size;
        }

        // Calculate size for this type
        long size = CalculateTypeSize(type);

        // Evict old entries BEFORE adding if cache is full to ensure we never exceed limit
        if (cache.Count >= _maxTypeCacheSize)
        {
            EvictOldestEntries(cache);
        }

        // Add to cache (may fail if another thread added it, which is fine)
        cache.TryAdd(type, new TypeSizeCacheEntry(size, accessOrder));

        return size;
    }

    private long CalculateTypeSize(Type type)
    {
        // Fast path: lookup in static frozen dictionary for primitives and common types
        if (TypeSizeLookupTable.TryGetValue(type, out long knownSize))
            return knownSize;

        // Handle nullable types not in the static dictionary
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null && TypeSizeLookupTable.TryGetValue(underlyingType, out long underlyingSize))
                return underlyingSize + 1; // Add 1 for hasValue flag
        }

        // For reference types, estimate based on fields/properties
        try
        {
            var fields = type.GetFields(System.Reflection.BindingFlags.Instance |
                                        System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.NonPublic);
            var properties = type.GetProperties(System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.Public);

            // Base object overhead + estimated field/property sizes
            return ObjectOverhead + ((long)fields.Length + properties.Length) * ReferenceSize;
        }
        catch (Exception ex)
        {
            // If reflection fails, return a reasonable default
            _logger.LogError(ex, "Reflection failed for type {TypeName}, using default size estimation", type.Name);
            return DefaultObjectSize;
        }
    }

    private void EvictOldestEntries(ConcurrentDictionary<Type, TypeSizeCacheEntry> cache)
    {
        // Use lock to prevent concurrent eviction from removing too many entries
        lock (_evictionLock)
        {
            // Double-check that eviction is still needed after acquiring lock
            if (cache.Count < _maxTypeCacheSize)
                return;

            // Simple eviction: remove entries with oldest access times
            // Remove ~10% of entries (minimum 1, maximum 100) to avoid frequent evictions
            // while also preventing massive evictions for very large caches
            int toRemove = Math.Clamp(_maxTypeCacheSize / EvictionPercentage, 1, MaxEvictionCount);
            long currentCounter = Interlocked.Read(ref _cacheAccessCounter);
            long threshold = currentCounter - (_maxTypeCacheSize - toRemove);
            int removed = 0;

            foreach (var kvp in cache)
            {
                long lastAccess = Interlocked.Read(ref kvp.Value.LastAccessField);
                if (lastAccess < threshold && cache.TryRemove(kvp.Key, out _))
                {
                    removed++;
                    // Stop once we've removed enough
                    if (removed >= toRemove)
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Cache entry for storing type size calculations with LRU tracking.
    /// Uses a mutable field for LastAccess to support Interlocked operations.
    /// </summary>
    private sealed record TypeSizeCacheEntry(long Size, long InitialLastAccess)
    {
        /// <summary>
        /// Mutable field for thread-safe LRU tracking via Interlocked operations.
        /// Initialized from InitialLastAccess in the primary constructor.
        /// </summary>
        public long LastAccessField = InitialLastAccess;
    }
}
