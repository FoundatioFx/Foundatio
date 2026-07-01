using System;
using System.Collections.Generic;
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

public sealed record MessageSubscriptionContext
{
    public required Type MessageType { get; init; }
    public required string Topic { get; init; }
    public string? OperationOverride { get; init; }
}

public interface IMessageRouter
{
    string ResolveRoute(MessageRouteContext context);
    string ResolveSubscription(MessageSubscriptionContext context);
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
    public string? SubscriptionIdentity { get; set; }
    public string? ServiceIdentity { get; set; }
    public Func<MessageRouteContext, string>? Convention { get; set; }

    public IReadOnlyList<DestinationDeclaration> GetTopologyDeclarations()
    {
        return TopologyDeclarations.ToArray();
    }

    internal void Declare(DestinationDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrEmpty(declaration.Name);

        bool exists = TopologyDeclarations.Any(d =>
            String.Equals(d.Name, declaration.Name, StringComparison.Ordinal)
            && d.Role == declaration.Role
            && String.Equals(d.Source, declaration.Source, StringComparison.Ordinal));

        if (!exists)
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

    public MessageRoutingOptionsBuilder UseSubscriptionIdentity(string subscription)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscription);
        _options.SubscriptionIdentity = subscription;
        RebuildSubscriptionDeclarations();
        return this;
    }

    public MessageRoutingOptionsBuilder UseServiceIdentity(string serviceIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceIdentity);
        _options.ServiceIdentity = serviceIdentity;
        RebuildSubscriptionDeclarations();
        return this;
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
        _options.Declare(new DestinationDeclaration { Name = destination, Role = DestinationRole.Queue });
    }

    private void DeclareTopic(string topic)
    {
        _options.Declare(new DestinationDeclaration { Name = topic, Role = DestinationRole.Topic });
        DeclareSubscription(topic);
    }

    private void RebuildSubscriptionDeclarations()
    {
        _options.RemoveDeclarations(d => d.Role == DestinationRole.Subscription);

        if (!String.IsNullOrEmpty(_options.DefaultPubSubTopic))
            DeclareSubscription(_options.DefaultPubSubTopic);

        foreach (string topic in _options.RouteMaps
            .Where(m => m.Role == MessageRouteRole.PubSubTopic)
            .Select(m => m.Route)
            .Distinct(StringComparer.Ordinal))
        {
            DeclareSubscription(topic);
        }
    }

    private void DeclareSubscription(string topic)
    {
        string? subscription = _options.SubscriptionIdentity ?? _options.ServiceIdentity;
        if (String.IsNullOrEmpty(subscription))
            return;

        DeclareSubscription(topic, subscription);
    }

    private void DeclareSubscription(string topic, string subscription)
    {
        _options.Declare(new DestinationDeclaration { Name = subscription, Role = DestinationRole.Subscription, Source = topic });
    }
}

public sealed class DefaultMessageRouter : IMessageRouter
{
    public static DefaultMessageRouter Instance { get; } = new(new MessageRoutingOptions());

    private readonly MessageRoutingOptions _options;

    public DefaultMessageRouter(MessageRoutingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string ResolveRoute(MessageRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.MessageType);

        if (!String.IsNullOrEmpty(context.OperationOverride))
            return context.OperationOverride;

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

    public string ResolveSubscription(MessageSubscriptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.MessageType);
        ArgumentException.ThrowIfNullOrEmpty(context.Topic);

        if (!String.IsNullOrEmpty(context.OperationOverride))
            return context.OperationOverride;

        if (!String.IsNullOrEmpty(_options.SubscriptionIdentity))
            return _options.SubscriptionIdentity;

        if (context.MessageType.GetCustomAttribute<MessageRouteAttribute>()?.Subscription is { Length: > 0 } subscription)
            return subscription;

        if (!String.IsNullOrEmpty(_options.ServiceIdentity))
            return _options.ServiceIdentity;

        return GetDefaultServiceIdentity();
    }


    private static string GetDefaultServiceIdentity()
    {
        string? configured = Environment.GetEnvironmentVariable("FOUNDATIO_SUBSCRIPTION_ID");
        if (!String.IsNullOrEmpty(configured))
            return configured;

        configured = Environment.GetEnvironmentVariable("FOUNDATIO_SERVICE_ID");
        if (!String.IsNullOrEmpty(configured))
            return configured;

        return MessageRoutingConventions.ToKebabCase(AppDomain.CurrentDomain.FriendlyName);
    }
}
