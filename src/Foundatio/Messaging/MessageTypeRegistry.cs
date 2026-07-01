using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>
/// Resolves the stable wire discriminator written to the <c>message.type</c> header in both directions: a CLR type to
/// its name (for sending) and a name back to its CLR type (so a grouped/interface consumer can deserialize the actual
/// payload type). Register stable names for types that may move between assemblies or namespaces; unregistered types
/// fall back to <see cref="Type.FullName"/> (never <c>AssemblyQualifiedName</c>).
/// </summary>
public interface IMessageTypeRegistry
{
    string GetName(Type messageType);
    Type? Resolve(string name);
}

public sealed record MessageTypeRegistration(string Name, Type MessageType);

public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly Dictionary<string, Type> _nameToType = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _typeToName = [];

    public MessageTypeRegistry(IEnumerable<MessageTypeRegistration>? registrations = null)
    {
        foreach (var registration in registrations ?? [])
            Add(registration);
    }

    public string GetName(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        return _typeToName.TryGetValue(messageType, out string? name)
            ? name
            : messageType.FullName ?? messageType.Name;
    }

    public Type? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_nameToType.TryGetValue(name, out var registered))
            return registered;

        var type = Type.GetType(name, throwOnError: false);
        if (type is not null)
            return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(name, throwOnError: false);
            if (type is not null)
                return type;
        }

        return null;
    }

    private void Add(MessageTypeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrEmpty(registration.Name);
        ArgumentNullException.ThrowIfNull(registration.MessageType);

        if (_nameToType.TryGetValue(registration.Name, out var existing) && existing != registration.MessageType)
            throw new InvalidOperationException($"Message type name \"{registration.Name}\" is already registered for \"{existing.FullName}\".");

        _nameToType[registration.Name] = registration.MessageType;
        _typeToName[registration.MessageType] = registration.Name;
    }
}
