using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Foundatio.Serializer;

namespace Foundatio.Utility;

/// <summary>
/// Indicates that a type exposes a data dictionary for storing arbitrary metadata.
/// </summary>
public interface IHaveData
{
    /// <summary>
    /// Gets the dictionary for storing arbitrary key-value data.
    /// </summary>
    IDictionary<string, object?> Data { get; }
}

public class DataDictionary : Dictionary<string, object?>
{
    public static readonly DataDictionary Empty = new();

    public DataDictionary() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public DataDictionary(IEnumerable<KeyValuePair<string, object?>>? values) : base(StringComparer.OrdinalIgnoreCase)
    {
        if (values != null)
        {
            foreach (var kvp in values)
                Add(kvp.Key, kvp.Value);
        }
    }
}

public static class DataDictionaryExtensions
{
    public static object? GetValueOrDefault(this IDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out object? value) ? value : null;
    }

    public static object? GetValueOrDefault(this IDictionary<string, object?> dictionary, string key, object? defaultValue)
    {
        return dictionary.TryGetValue(key, out object? value) ? value : defaultValue;
    }

    public static object? GetValueOrDefault(this IDictionary<string, object?> dictionary, string key, Func<object?> defaultValueProvider)
    {
        return dictionary.TryGetValue(key, out object? value) ? value : defaultValueProvider();
    }

    public static T? GetValue<T>(this IDictionary<string, object?> dictionary, string key)
    {
        if (!dictionary.ContainsKey(key))
            throw new KeyNotFoundException($"Key \"{key}\" not found in the dictionary");

        return dictionary.GetValueOrDefault<T>(key);
    }

    public static T? GetValueOrDefault<T>(this IDictionary<string, object?> dictionary, string key, T? defaultValue = default)
    {
        if (!dictionary.TryGetValue(key, out object? data))
            return defaultValue;

        switch (data)
        {
            case T t:
                return t;
            case null:
                return default;
            default:
                try
                {
                    return data.ToType<T>();
                }
                catch
                {
                    return defaultValue;
                }
        }
    }

    public static string? GetString(this IDictionary<string, object?> dictionary, string name)
    {
        return dictionary.GetString(name, null);
    }

    public static string? GetString(this IDictionary<string, object?> dictionary, string name, string? @default)
    {
        if (!dictionary.TryGetValue(name, out object? value))
            return @default;

        if (value is null)
            return null;

        if (value is string s)
            return s;

        return @default;
    }

    public static bool GetBoolean(this IDictionary<string, object?> dictionary, string name)
    {
        return dictionary.GetBoolean(name, false);
    }

    public static bool GetBoolean(this IDictionary<string, object?> dictionary, string name, bool @default)
    {
        if (!dictionary.TryGetValue(name, out object? value))
            return @default;

        if (value is bool b)
            return b;

        string? valueString = value?.ToString();
        if (valueString is not null && Boolean.TryParse(valueString, out bool result))
            return result;

        return @default;
    }
}

public static class HaveDataExtensions
{
    /// <summary>
    /// Will get a value from the data dictionary and attempt to convert it to the target type using various type conversions
    /// as well as using either the passed in <see cref="ISerializer"/> or one accessed from the <see cref="IHaveSerializer"/> accessor.
    /// </summary>
    /// <typeparam name="T">The data type to be returned</typeparam>
    /// <param name="target">The source containing the data</param>
    /// <param name="key">They data dictionary key</param>
    /// <param name="defaultValue">The default value to return if the value doesn't exist</param>
    /// <param name="serializer">The serializer to use to convert the type from <see cref="String"/> or <see cref="Byte"/> array</param>
    /// <returns>The value from the data dictionary converted to the desired type</returns>
    public static T? GetDataOrDefault<T>(this IHaveData target, string key, T? defaultValue = default, ISerializer? serializer = null)
    {
        if (serializer is null && target is IHaveSerializer haveSerializer)
            serializer = haveSerializer.Serializer;

        if (target.Data.TryGetValue(key, out object? value))
            return value.ToType<T>(serializer);

        return defaultValue;
    }

    /// <summary>
    /// Will get a value from the data dictionary and attempt to convert it to the target type using various type conversions
    /// as well as using either the passed in <see cref="ISerializer"/> or one accessed from the <see cref="IHaveSerializer"/> accessor.
    /// </summary>
    /// <typeparam name="T">The data type to be returned</typeparam>
    /// <param name="target">The source containing the data</param>
    /// <param name="key">They data dictionary key</param>
    /// <param name="value">The value from the data dictionary converted to the desired type</param>
    /// <param name="serializer">The serializer to use to convert the type from <see cref="String"/> or <see cref="Byte"/> array</param>
    /// <returns>Whether or not we successfully got and converted the data</returns>
    public static bool TryGetData<T>(this IHaveData target, string key, [MaybeNull] out T value, ISerializer? serializer = null)
    {
        if (serializer is null && target is IHaveSerializer haveSerializer)
            serializer = haveSerializer.Serializer;

        if (!target.Data.TryGetValue(key, out object? dataValue))
        {
            value = default;
            return false;
        }

        var converted = dataValue.ToType<T>(serializer);
        if (converted is null)
        {
            value = default;
            return false;
        }

        value = converted;
        return true;
    }
}
