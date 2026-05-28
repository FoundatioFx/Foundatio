using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;

namespace Foundatio.Messaging;

internal static class EnumExtensions
{
    private static readonly ConcurrentDictionary<Enum, string> Cache = new();

    public static string ToEnumString<T>(this T value) where T : struct, Enum
    {
        return Cache.GetOrAdd(value, static v =>
        {
            var member = v.GetType().GetField(v.ToString()!);
            var attribute = member?.GetCustomAttribute<EnumMemberAttribute>();
            return attribute?.Value ?? v.ToString()!;
        });
    }
}
