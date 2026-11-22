using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Messaging;

internal sealed class MessageHandlerRegistration
{
    public required Type MessageType { get; init; }
    public required MessageHandlerKind Kind { get; init; }
    public required string HandlerName { get; init; }

    /// <summary>
    /// False for a delegate, which is not resolved from the services passed to <see cref="Invoke"/>: its scope is then
    /// created only if it reads <see cref="IMessageContext.Services"/>.
    /// </summary>
    public bool ResolvesFromScope { get; init; } = true;

    /// <summary>The handler class, or null for a delegate handler.</summary>
    public Type? HandlerType { get; init; }
    public required Func<IServiceProvider, object, IMessageContext, CancellationToken, Task> Invoke { get; init; }

    /// <summary>The options deliveries use: <see cref="ConfiguredOptions"/>, with the test harness's retry delay if it sets one.</summary>
    public HandlerOptions Options { get; private set; } = new();

    /// <summary>The options as registered, or as <see cref="OptionsFactory"/> returned them. Handlers that share a source must agree on them.</summary>
    public HandlerOptions ConfiguredOptions { get; private set; } = new();

    /// <summary>Creates the options from the app's services when the runtime is built, for options registered with a factory.</summary>
    public Func<IServiceProvider, HandlerOptions>? OptionsFactory { get; private init; }

    public SubscriptionScope Scope { get; private set; } = SubscriptionScope.Service;
    public string? SubscriptionName { get; private set; }

    /// <summary>
    /// Calls <see cref="OptionsFactory"/>, validating what it returns with <paramref name="validate"/>, and applies
    /// <paramref name="retryDelay"/> to the handler's own retry policy. Runs when the runtime is built.
    /// </summary>
    public void Resolve(IServiceProvider services, Func<HandlerOptions, HandlerOptions> validate, TimeSpan? retryDelay)
    {
        if (OptionsFactory is not null)
        {
            var options = validate(OptionsFactory(services) ?? throw new InvalidOperationException($"The options factory of {HandlerName} returned null."));
            SetOptions(options);
        }

        Options = retryDelay is { } delay && ConfiguredOptions.RetryPolicy is { } policy
            ? ConfiguredOptions with { RetryPolicy = policy with { Delay = Resilience.RetryPolicy.Fixed(delay) } }
            : ConfiguredOptions;
    }

    private void SetOptions(HandlerOptions options)
    {
        Options = ConfiguredOptions = options;
        var subscription = options as SubscriptionOptions;
        Scope = subscription?.Scope ?? SubscriptionScope.Service;
        SubscriptionName = subscription?.Name;
    }

    public static IEnumerable<MessageHandlerRegistration> ForHandlerType(Type handlerType, MessageHandlerKind kind, HandlerOptions options, Func<IServiceProvider, HandlerOptions>? optionsFactory = null)
    {
        var messageTypes = handlerType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToList();

        if (handlerType.IsAbstract || handlerType.IsInterface)
            throw new ArgumentException($"{handlerType.FullName} must be a concrete class.", nameof(handlerType));
        if (messageTypes.Count == 0)
            throw new ArgumentException($"{handlerType.FullName} does not implement IMessageHandler<T>.", nameof(handlerType));

        foreach (var messageType in messageTypes)
        {
            var invoke = (Func<IServiceProvider, object, IMessageContext, CancellationToken, Task>)typeof(MessageHandlerRegistration)
                .GetMethod(nameof(CreateInvoker), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(handlerType, messageType)
                .Invoke(null, null)!;

            var registration = new MessageHandlerRegistration
            {
                MessageType = messageType,
                Kind = kind,
                HandlerName = handlerType.Name,
                HandlerType = handlerType,
                OptionsFactory = optionsFactory,
                Invoke = invoke
            };
            registration.SetOptions(options);
            yield return registration;
        }
    }

    private static Func<IServiceProvider, object, IMessageContext, CancellationToken, Task> CreateInvoker<THandler, T>()
        where THandler : class, IMessageHandler<T> where T : class
    {
        return (services, message, context, cancellationToken) =>
            ActivatorUtilities.GetServiceOrCreateInstance<THandler>(services).HandleAsync((T)message, context, cancellationToken);
    }

    public static MessageHandlerRegistration ForDelegate<T>(Func<T, IMessageContext, CancellationToken, Task> handler, MessageHandlerKind kind, HandlerOptions options) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = new MessageHandlerRegistration
        {
            MessageType = typeof(T),
            Kind = kind,
            HandlerName = $"{typeof(T).Name} delegate",
            ResolvesFromScope = false,
            Invoke = (_, message, context, cancellationToken) => handler((T)message, context, cancellationToken)
        };
        registration.SetOptions(options);
        return registration;
    }
}
