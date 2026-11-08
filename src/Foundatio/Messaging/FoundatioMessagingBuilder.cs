using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foundatio.Messaging;

/// <summary>Configures Foundatio messaging. Every call to <c>AddFoundatioMessaging</c> adds to the same configuration.</summary>
public sealed class FoundatioMessagingBuilder
{
    private readonly MessagingRegistrations _registrations;

    internal FoundatioMessagingBuilder(IServiceCollection services, MessagingRegistrations registrations)
    {
        Services = services;
        _registrations = registrations;
    }

    public IServiceCollection Services { get; }

    /// <summary>Uses a process-local transport. Messages are lost when the process stops.</summary>
    public FoundatioMessagingBuilder UseInMemory()
    {
        return UseTransport(sp => new InMemoryMessageTransport(sp.GetService<TimeProvider>()));
    }

    /// <summary>Uses a transport you create and dispose yourself, for example one shared by several hosts in a test.</summary>
    public FoundatioMessagingBuilder UseTransport(IMessageTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        Services.RemoveAll<IMessageTransport>();
        Services.AddSingleton(transport);
        return this;
    }

    /// <summary>Uses a transport created, and disposed, by the service provider.</summary>
    public FoundatioMessagingBuilder UseTransport(Func<IServiceProvider, IMessageTransport> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.ReplaceSingleton(factory);
        return this;
    }

    /// <summary>Names this service's durable subscriptions. Defaults to the host's application name.</summary>
    public FoundatioMessagingBuilder UseServiceName(string serviceName)
    {
        MessageTypeRegistry.ValidateName(serviceName, nameof(serviceName));
        return Configure(o => o.ServiceName = serviceName);
    }

    public FoundatioMessagingBuilder Configure(Action<MessagingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _registrations.ConfigureOptions.Add(configure);
        return this;
    }

    /// <summary>
    /// Also provides the original <see cref="IMessageBus"/>, <see cref="IMessagePublisher"/> and <see cref="IMessageSubscriber"/>
    /// on this transport, so existing code, including <c>HybridCacheClient</c> and <c>CacheLockProvider</c>, keeps working.
    /// Every running instance receives every message, as before.
    /// </summary>
    public FoundatioMessagingBuilder AddLegacyMessageBus(Action<SharedMessageBusOptions>? configure = null)
    {
        Services.ReplaceSingleton<IMessageBus>(sp =>
        {
            var options = new SharedMessageBusOptions();
            if (sp.GetService<Foundatio.Serializer.ISerializer>() is { } serializer)
                options.Serializer = serializer;
            if (sp.GetService<TimeProvider>() is { } timeProvider)
                options.TimeProvider = timeProvider;
            if (sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() is { } loggerFactory)
                options.LoggerFactory = loggerFactory;
            configure?.Invoke(options);
            return new LegacyMessageBusAdapter(sp.GetRequiredService<IMessageTransport>(), options);
        });
        Services.ReplaceSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
        Services.ReplaceSingleton<IMessageSubscriber>(sp => sp.GetRequiredService<IMessageBus>());
        return this;
    }

    /// <summary>Gives <typeparamref name="T"/> a stable wire name, and optionally its queue and topic. Same as <see cref="MessageAttribute"/>.</summary>
    public FoundatioMessagingBuilder MapMessage<T>(string name, string? queue = null, string? topic = null) where T : class
    {
        MessageTypeRegistry.ValidateName(name, nameof(name));
        _registrations.Maps.Add((typeof(T), name, queue, topic));
        return this;
    }

    /// <summary>
    /// Handles messages sent with <see cref="IMessageClient.SendAsync{T}"/> for every <see cref="IMessageHandler{T}"/>
    /// that <typeparamref name="THandler"/> implements. Replicas compete, so each message is handled once.
    /// </summary>
    public FoundatioMessagingBuilder AddConsumer<THandler>(HandlerOptions? options = null) where THandler : class
    {
        return AddHandlers(MessageHandlerRegistration.ForHandlerType(typeof(THandler), MessageHandlerKind.Consumer, Validate(options ?? new HandlerOptions())).ToList());
    }

    /// <summary>Handles messages sent with <see cref="IMessageClient.SendAsync{T}"/> using a delegate.</summary>
    public FoundatioMessagingBuilder AddConsumer<T>(Func<T, IMessageContext, CancellationToken, Task> handler, HandlerOptions? options = null) where T : class
    {
        return AddHandlers([MessageHandlerRegistration.ForDelegate(handler, MessageHandlerKind.Consumer, Validate(options ?? new HandlerOptions()))]);
    }

    /// <summary>
    /// Handles events published with <see cref="IMessageClient.PublishAsync{T}"/> for every <see cref="IMessageHandler{T}"/>
    /// that <typeparamref name="THandler"/> implements. Each service gets its own copy; use
    /// <see cref="SubscriptionScope.Instance"/> to give every running instance a copy.
    /// </summary>
    public FoundatioMessagingBuilder AddSubscriber<THandler>(SubscriptionOptions? options = null) where THandler : class
    {
        return AddHandlers(MessageHandlerRegistration.ForHandlerType(typeof(THandler), MessageHandlerKind.Subscriber, Validate(options ?? new SubscriptionOptions())).ToList());
    }

    /// <summary>Handles events published with <see cref="IMessageClient.PublishAsync{T}"/> using a delegate.</summary>
    public FoundatioMessagingBuilder AddSubscriber<T>(Func<T, IMessageContext, CancellationToken, Task> handler, SubscriptionOptions? options = null) where T : class
    {
        return AddHandlers([MessageHandlerRegistration.ForDelegate(handler, MessageHandlerKind.Subscriber, Validate(options ?? new SubscriptionOptions()))]);
    }

    private FoundatioMessagingBuilder AddHandlers(System.Collections.Generic.IReadOnlyList<MessageHandlerRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (registration.Kind == MessageHandlerKind.Consumer && _registrations.Handlers.Any(h => h.Kind == MessageHandlerKind.Consumer && h.MessageType == registration.MessageType))
                throw new ArgumentException($"{registration.MessageType.Name} already has a consumer. A sent message is handled by exactly one consumer; use AddSubscriber for events several handlers react to.");

            _registrations.Handlers.Add(registration);
        }

        return this;
    }

    private static T Validate<T>(T options) where T : HandlerOptions
    {
        options.Validate();
        (options.RetryPolicy)?.Validate();
        if (options is SubscriptionOptions { Name: { } name })
            MessageTypeRegistry.ValidateName(name, nameof(SubscriptionOptions.Name));

        return options;
    }
}
