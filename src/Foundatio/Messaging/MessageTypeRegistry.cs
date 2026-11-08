using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>Maps message types to stable wire names and back. Only known types are ever deserialized.</summary>
internal sealed class MessageTypeRegistry
{
    private readonly ConcurrentDictionary<Type, MessageRoute> _routes = new();
    private readonly ConcurrentDictionary<string, Type> _types = new(StringComparer.Ordinal);
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
