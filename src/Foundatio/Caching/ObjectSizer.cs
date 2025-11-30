using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching;

/// <summary>
/// Calculates the estimated memory size of objects. Uses fast paths for common types
/// and falls back to JSON serialization for complex objects.
/// </summary>
/// <remarks>
/// The type size cache is bounded to prevent unbounded memory growth. When the cache
/// reaches its limit, older entries are evicted. Call <see cref="Dispose"/> to clear
/// the cache when the sizer is no longer needed.
/// </remarks>
public class ObjectSizer : IDisposable
{
    private ConcurrentDictionary<Type, TypeSizeCacheEntry> _typeSizeCache;
    private readonly int _maxTypeCacheSize;
    private long _cacheAccessCounter;
    private bool _disposed;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 64
    };

    private readonly ILogger _logger;

    private const int DefaultMaxTypeCacheSize = 1000;

    /// <summary>
    /// Creates a new ObjectSizer instance with default settings.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public ObjectSizer(ILoggerFactory loggerFactory) : this(DefaultMaxTypeCacheSize, loggerFactory)
    {
    }

    /// <summary>
    /// Creates a new ObjectSizer instance.
    /// </summary>
    /// <param name="maxTypeCacheSize">Maximum number of types to cache size calculations for. Default is 1000.</param>
    /// <param name="loggerFactory">Optional logger factory for diagnostic logging.</param>
    public ObjectSizer(int maxTypeCacheSize = DefaultMaxTypeCacheSize, ILoggerFactory loggerFactory = null)
    {
        _maxTypeCacheSize = maxTypeCacheSize > 0 ? maxTypeCacheSize : 1000;
        // Pre-size dictionary to avoid resizing; -1 uses default concurrency level
        _typeSizeCache = new ConcurrentDictionary<Type, TypeSizeCacheEntry>(-1, _maxTypeCacheSize);

        _logger = loggerFactory?.CreateLogger<ObjectSizer>() ?? NullLogger<ObjectSizer>.Instance;
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
    /// <exception cref="ObjectDisposedException">Thrown if the sizer has been disposed.</exception>
    public long CalculateSize(object value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (value == null)
            return 8; // Reference size

        var type = value.GetType();

        // Fast paths for common types - no caching needed
        switch (value)
        {
            case string str:
                return 24 + (str.Length * 2); // Object overhead + UTF-16 chars
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
                return 16;
        }

        // Handle nullable types
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlyingType = Nullable.GetUnderlyingType(type);
            return GetCachedTypeSize(underlyingType) + 1; // Add 1 for hasValue flag
        }

        // Handle arrays efficiently
        if (value is Array array)
        {
            long size = 24; // Array overhead
            var elementType = array.GetType().GetElementType();
            var elementSize = GetCachedTypeSize(elementType);

            // For primitive arrays, we can calculate size efficiently
            if (elementSize > 0 && array.Length > 0 && (elementType.IsPrimitive || elementType == typeof(string)))
            {
                size += array.Length * elementSize;
                if (elementType == typeof(string))
                {
                    // For string arrays, we need to account for actual string lengths
                    foreach (string str in array)
                    {
                        if (str != null)
                            size += str.Length * 2 - elementSize; // Adjust for actual string size
                    }
                }
                return size;
            }
        }

        // Handle collections with sampling for large ones
        if (value is IEnumerable enumerable && !(value is string))
        {
            long size = 32; // Collection overhead
            int count = 0;
            long itemSizeSum = 0;
            const int sampleLimit = 50; // Sample first 50 items for performance

            foreach (var item in enumerable)
            {
                count++;
                itemSizeSum += CalculateSize(item);
                if (count >= sampleLimit)
                {
                    // Estimate based on sample
                    if (enumerable is ICollection collection)
                    {
                        var avgItemSize = itemSizeSum / count;
                        return size + (collection.Count * avgItemSize) + (collection.Count * 8); // refs
                    }
                    break;
                }
            }

            return size + itemSizeSum + (count * 8); // Collection overhead + items + reference overhead
        }

        // Fall back to JSON serialization for complex objects
        try
        {
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
            return jsonBytes.Length + 24; // JSON size + object overhead
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
        if (cache == null)
            return 64; // Default if disposed

        var accessOrder = Interlocked.Increment(ref _cacheAccessCounter);

        if (cache.TryGetValue(type, out var entry))
        {
            // Update access time for LRU
            entry.LastAccess = accessOrder;
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
        cache.TryAdd(type, new TypeSizeCacheEntry { Size = size, LastAccess = accessOrder });

        return size;
    }

    private long CalculateTypeSize(Type type)
    {
        // Handle primitive types
        if (type.IsPrimitive)
        {
            return type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ? 1L :
                   type == typeof(char) || type == typeof(short) || type == typeof(ushort) ? 2L :
                   type == typeof(int) || type == typeof(uint) || type == typeof(float) ? 4L :
                   type == typeof(long) || type == typeof(ulong) || type == typeof(double) ? 8L : 8L;
        }

        // Handle common value types
        if (type == typeof(decimal) || type == typeof(Guid)) return 16;
        if (type == typeof(DateTime) || type == typeof(TimeSpan)) return 8;

        // For reference types, estimate based on fields/properties
        try
        {
            var fields = type.GetFields(System.Reflection.BindingFlags.Instance |
                                        System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.NonPublic);
            var properties = type.GetProperties(System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.Public);

            // Base object overhead + estimated field/property sizes
            return 24 + ((fields.Length + properties.Length) * 8);
        }
        catch (Exception ex)
        {
            // If reflection fails, return a reasonable default
            _logger.LogError(ex, "Reflection failed for type {TypeName}, using default size estimation", type.Name);
            return 64;
        }
    }

    private void EvictOldestEntries(ConcurrentDictionary<Type, TypeSizeCacheEntry> cache)
    {
        // Simple eviction: remove entries with oldest access times
        // Remove ~10% of entries (minimum 1, maximum 100) to avoid frequent evictions
        // while also preventing massive evictions for very large caches
        int toRemove = Math.Clamp(_maxTypeCacheSize / 10, 1, 100);
        var threshold = _cacheAccessCounter - (_maxTypeCacheSize - toRemove);

        foreach (var kvp in cache)
        {
            if (kvp.Value.LastAccess < threshold)
            {
                cache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class TypeSizeCacheEntry
    {
        public long Size { get; init; }
        public long LastAccess { get; set; }
    }
}
