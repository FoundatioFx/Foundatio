using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Foundatio.Messaging;

public enum MessageRouteRole
{
    QueueDestination,
    PubSubTopic
}

public sealed record MessageRouteContext
{
    public required Type MessageType { get; init; }
    public required MessageRouteRole Role { get; init; }
    public string? OperationOverride { get; init; }
}

public interface IMessageRouter
{
    string ResolveRoute(MessageRouteContext context);
}

public sealed record MessageRouteMap
{
    public required Type MessageType { get; init; }
    public required MessageRouteRole Role { get; init; }
    public required string Route { get; init; }
}

public sealed class MessageRoutingOptions
{
    internal List<MessageRouteMap> RouteMaps { get; } = [];
    internal List<DestinationDeclaration> TopologyDeclarations { get; } = [];

    public string? DefaultQueueDestination { get; set; }
    public string? DefaultPubSubTopic { get; set; }
    public Func<MessageRouteContext, string>? Convention { get; set; }

    /// <summary>Returns the declared type-to-route mappings for configuration diagnostics.</summary>
    public IReadOnlyList<MessageRouteMap> GetRouteMaps() => RouteMaps.ToArray();

    public IReadOnlyList<DestinationDeclaration> GetTopologyDeclarations()
    {
        return TopologyDeclarations.ToArray();
    }

    internal void Declare(DestinationDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (!TopologyDeclarations.Any(d => d.Address == declaration.Address))
            TopologyDeclarations.Add(declaration);
    }

    internal void RemoveDeclarations(Predicate<DestinationDeclaration> match)
    {
        TopologyDeclarations.RemoveAll(match);
    }
}

public sealed class MessageRoutingOptionsBuilder
{
    private readonly MessageRoutingOptions _options;

    public MessageRoutingOptionsBuilder()
        : this(new MessageRoutingOptions())
    {
    }

    internal MessageRoutingOptionsBuilder(MessageRoutingOptions options)
    {
        _options = options;
    }

    public MessageRoutingOptionsBuilder UseDefaultQueue(string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        _options.DefaultQueueDestination = destination;
        DeclareQueue(destination);
        return this;
    }

    public MessageRoutingOptionsBuilder UseDefaultTopic(string topic)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        _options.DefaultPubSubTopic = topic;
        DeclareTopic(topic);
        return this;
    }

    public MessageRoutingOptionsBuilder MapQueue<T>(string destination)
    {
        return MapQueue(typeof(T), destination);
    }

    public MessageRoutingOptionsBuilder MapQueue(Type messageType, string destination)
    {
        return Map(MessageRouteRole.QueueDestination, destination, messageType);
    }

    public MessageRoutingOptionsBuilder MapQueue(string destination, params Type[] messageTypes)
    {
        return Map(MessageRouteRole.QueueDestination, destination, messageTypes);
    }

    public MessageRoutingOptionsBuilder MapTopic<T>(string topic)
    {
        return MapTopic(typeof(T), topic);
    }

    public MessageRoutingOptionsBuilder MapTopic(Type messageType, string topic)
    {
        return Map(MessageRouteRole.PubSubTopic, topic, messageType);
    }

    public MessageRoutingOptionsBuilder MapTopic(string topic, params Type[] messageTypes)
    {
        return Map(MessageRouteRole.PubSubTopic, topic, messageTypes);
    }

    public MessageRoutingOptionsBuilder UseConvention(Func<MessageRouteContext, string> convention)
    {
        _options.Convention = convention ?? throw new ArgumentNullException(nameof(convention));
        return this;
    }


    public MessageRoutingOptions Build()
    {
        return _options;
    }

    private MessageRoutingOptionsBuilder Map(MessageRouteRole role, string route, params Type[] messageTypes)
    {
        ArgumentException.ThrowIfNullOrEmpty(route);
        ArgumentNullException.ThrowIfNull(messageTypes);

        if (messageTypes.Length == 0)
            throw new ArgumentException("At least one message type is required.", nameof(messageTypes));

        foreach (var messageType in messageTypes)
        {
            ArgumentNullException.ThrowIfNull(messageType);
            _options.RouteMaps.Add(new MessageRouteMap
            {
                MessageType = messageType,
                Role = role,
                Route = route
            });
        }

        if (role == MessageRouteRole.QueueDestination)
            DeclareQueue(route);
        else
            DeclareTopic(route);

        return this;
    }

    private void DeclareQueue(string destination)
    {
        _options.Declare(new DestinationDeclaration { Address = DestinationAddress.ForQueue(destination) });
    }

    private void DeclareTopic(string topic)
    {
        _options.Declare(new DestinationDeclaration { Address = DestinationAddress.ForTopic(topic) });
    }

}

public sealed class DefaultMessageRouter : IMessageRouter
{
    public static DefaultMessageRouter Instance { get; } = new(new MessageRoutingOptions());

    private readonly MessageRoutingOptions _options;
    private readonly ConcurrentDictionary<(Type Type, MessageRouteRole Role), string> _routes = new();

    public DefaultMessageRouter(MessageRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = new MessageRoutingOptions
        {
            DefaultQueueDestination = options.DefaultQueueDestination,
            DefaultPubSubTopic = options.DefaultPubSubTopic,
            Convention = options.Convention
        };
        _options.RouteMaps.AddRange(options.RouteMaps);
    }

    public string ResolveRoute(MessageRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.MessageType);

        if (!String.IsNullOrEmpty(context.OperationOverride))
            return context.OperationOverride;

        if (_options.Convention is not null)
            return ResolveUncached(context);
        return _routes.GetOrAdd((context.MessageType, context.Role), key => ResolveUncached(new MessageRouteContext { MessageType = key.Type, Role = key.Role }));
    }

    private string ResolveUncached(MessageRouteContext context)
    {
        var exact = _options.RouteMaps.LastOrDefault(m => m.Role == context.Role && m.MessageType == context.MessageType);
        if (exact is not null)
            return exact.Route;

        var assignable = _options.RouteMaps.LastOrDefault(m => m.Role == context.Role && m.MessageType != context.MessageType && m.MessageType.IsAssignableFrom(context.MessageType));
        if (assignable is not null)
            return assignable.Route;

        var attribute = context.MessageType.GetCustomAttribute<MessageRouteAttribute>();
        string? attributedRoute = context.Role == MessageRouteRole.QueueDestination
            ? attribute?.Destination
            : attribute?.Topic ?? attribute?.Destination;

        if (!String.IsNullOrEmpty(attributedRoute))
            return attributedRoute;

        string? configuredDefault = context.Role == MessageRouteRole.QueueDestination
            ? _options.DefaultQueueDestination
            : _options.DefaultPubSubTopic;

        if (!String.IsNullOrEmpty(configuredDefault))
            return configuredDefault;

        if (_options.Convention is not null)
        {
            string convention = _options.Convention(context);
            if (!String.IsNullOrEmpty(convention))
                return convention;
        }

        return MessageRoutingConventions.ToKebabCase(context.MessageType.Name);
    }

}
