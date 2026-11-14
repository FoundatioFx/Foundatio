using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>Maps message types to stable wire names and back. Only known types are ever deserialized.</summary>
internal sealed class MessageTypeRegistry
{
    private readonly ConcurrentDictionary<Type, MessageRoute> _routes = new();
    private readonly ConcurrentDictionary<string, Type> _types = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, string?> _baseTypeNames = new();
    private readonly object _lock = new();

    public void Map(Type type, string name, string? queue = null, string? topic = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        var route = new MessageRoute(type, ValidateName(name, nameof(name)), ValidateName(queue ?? name, nameof(queue)), ValidateName(topic ?? name, nameof(topic)));

        lock (_lock)
        {
            if (_routes.TryGetValue(type, out var existing) && existing != route)
                throw new ArgumentException($"Message type {type.FullName} is already mapped to \"{existing.Name}\".", nameof(type));

            Register(route);
        }
    }

    public MessageRoute GetRoute(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_routes.TryGetValue(type, out var route))
            return route;

        var attribute = type.GetCustomAttribute<MessageAttribute>(inherit: false);
        string name = attribute?.Name is { } attributeName ? ValidateName(attributeName, nameof(MessageAttribute.Name)) : GetDefaultName(type);
        route = new MessageRoute(type, name,
            attribute?.Queue is { } queue ? ValidateName(queue, nameof(MessageAttribute.Queue)) : name,
            attribute?.Topic is { } topic ? ValidateName(topic, nameof(MessageAttribute.Topic)) : name);

        lock (_lock)
        {
            if (_routes.TryGetValue(type, out var existing))
                return existing;

            Register(route);
            return route;
        }
    }

    public bool TryGetType(string name, out Type type) => _types.TryGetValue(name, out type!);

    /// <summary>
    /// The names of <paramref name="type"/>'s base classes, most derived first, then its interfaces, separated by
    /// commas, for <see cref="KnownHeaders.MessageBaseTypes"/>. Framework types are left out. Null when there are none.
    /// </summary>
    public string? GetBaseTypeNames(Type type) => _baseTypeNames.GetOrAdd(type, ComputeBaseTypeNames);

    /// <summary>Finds the first type named by <paramref name="headers"/> that is known here: the message type, then its base types.</summary>
    public bool TryGetType(IReadOnlyDictionary<string, string> headers, out Type type)
    {
        if (headers.TryGetValue(KnownHeaders.MessageType, out string? name) && TryGetType(name, out type))
            return true;

        if (headers.TryGetValue(KnownHeaders.MessageBaseTypes, out string? baseTypes))
        {
            foreach (string baseType in baseTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryGetType(baseType, out type))
                    return true;
            }
        }

        type = null!;
        return false;
    }

    public static string ValidateName(string name, string parameterName) => TransportNames.Validate(name, parameterName);

    /// <summary>Turns any string (a service or type name) into a valid name.</summary>
    public static string NormalizeName(string value) => TransportNames.Normalize(value);

    private void Register(MessageRoute route)
    {
        if (_types.TryGetValue(route.Name, out var other) && other != route.Type)
            throw new MessageBusException($"Message types {other.FullName} and {route.Type.FullName} both use the name \"{route.Name}\". Give one of them a unique name with [Message(\"...\")].");

        _routes[route.Type] = route;
        _types[route.Name] = route.Type;
    }

    private string? ComputeBaseTypeNames(Type type)
    {
        var names = new List<string>();
        for (var baseType = type.BaseType; baseType is not null && IsApplicationType(baseType); baseType = baseType.BaseType)
            AddName(baseType);

        foreach (var contract in type.GetInterfaces().Where(IsApplicationType).OrderBy(i => i.FullName, StringComparer.Ordinal))
            AddName(contract);

        return names.Count == 0 ? null : String.Join(",", names);

        void AddName(Type baseType)
        {
            if (TryGetName(baseType) is { } name && !names.Contains(name))
                names.Add(name);
        }
    }

    /// <summary>The wire name a type has, or would have once used, without registering it. Null when its attribute names it invalidly.</summary>
    private string? TryGetName(Type type)
    {
        if (_routes.TryGetValue(type, out var route))
            return route.Name;

        try
        {
            return type.GetCustomAttribute<MessageAttribute>(inherit: false)?.Name is { } name ? ValidateName(name, nameof(MessageAttribute.Name)) : GetDefaultName(type);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsApplicationType(Type type)
    {
        if (type == typeof(object))
            return false;

        string? ns = type.Namespace;
        return ns is null || !(ns is "System" or "Microsoft" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    private static string GetDefaultName(Type type)
    {
        string name = (type.FullName ?? type.Name).Replace('+', '.');
        if (type.IsGenericType)
        {
            name = name[..name.IndexOf('`')];
            name += "-" + String.Join("-", type.GetGenericArguments().Select(GetDefaultName));
        }

        return NormalizeName(name);
    }

}
