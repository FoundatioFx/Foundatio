using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Foundatio.Caching;

public static class ObjectSizer
{
    private static readonly ConcurrentDictionary<Type, long> _typeSizeCache = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() 
    { 
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Default size calculation function that uses fast paths for common types and falls back to JSON serialization.
    /// </summary>
    public static readonly Func<object, long> Default = CalculateSize;

    public static long CalculateSize(object value)
    {
        if (value == null)
            return 8; // Reference size

        var type = value.GetType();

        // Fast paths for common types
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
            
            if (elementSize > 0 && array.Length > 0)
            {
                // For primitive arrays, we can calculate size efficiently
                if (elementType.IsPrimitive || elementType == typeof(string))
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
        catch
        {
            // If JSON serialization fails, fall back to cached type size estimation
            return GetCachedTypeSize(type);
        }
    }

    private static long GetCachedTypeSize(Type type)
    {
        return _typeSizeCache.GetOrAdd(type, t =>
        {
            // Handle primitive types
            if (t.IsPrimitive)
            {
                return t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte) ? 1L :
                       t == typeof(char) || t == typeof(short) || t == typeof(ushort) ? 2L :
                       t == typeof(int) || t == typeof(uint) || t == typeof(float) ? 4L :
                       t == typeof(long) || t == typeof(ulong) || t == typeof(double) ? 8L : 8L;
            }

            // Handle common value types
            if (t == typeof(decimal) || t == typeof(Guid)) return 16;
            if (t == typeof(DateTime) || t == typeof(TimeSpan)) return 8;

            // For reference types, estimate based on fields/properties
            try
            {
                var fields = t.GetFields(System.Reflection.BindingFlags.Instance | 
                                       System.Reflection.BindingFlags.Public | 
                                       System.Reflection.BindingFlags.NonPublic);
                var properties = t.GetProperties(System.Reflection.BindingFlags.Instance | 
                                               System.Reflection.BindingFlags.Public);
                
                // Base object overhead + estimated field/property sizes
                return 24 + ((fields.Length + properties.Length) * 8);
            }
            catch
            {
                // If reflection fails, return a reasonable default
                return 64;
            }
        });
    }
}