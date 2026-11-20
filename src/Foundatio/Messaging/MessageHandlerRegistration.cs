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
    public required Func<IServiceProvider, object, IMessageContext, CancellationToken, Task> Invoke { get; init; }
    public HandlerOptions Options { get; init; } = new();
    public SubscriptionScope Scope { get; init; } = SubscriptionScope.Service;
    public string? SubscriptionName { get; init; }

    public static IEnumerable<MessageHandlerRegistration> ForHandlerType(Type handlerType, MessageHandlerKind kind, HandlerOptions options)
    {
        var messageTypes = handlerType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToList();

        if (handlerType.IsAbstract || handlerType.IsInterface)
            throw new ArgumentException($"{handlerType.FullName} must be a concrete class.", nameof(handlerType));
        if (messageTypes.Count == 0)
            throw new ArgumentException($"{handlerType.FullName} does not implement IMessageHandler<T>.", nameof(handlerType));

        var subscription = options as SubscriptionOptions;
        foreach (var messageType in messageTypes)
        {
            var invoke = (Func<IServiceProvider, object, IMessageContext, CancellationToken, Task>)typeof(MessageHandlerRegistration)
                .GetMethod(nameof(CreateInvoker), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(handlerType, messageType)
                .Invoke(null, null)!;

            yield return new MessageHandlerRegistration
            {
                MessageType = messageType,
                Kind = kind,
                HandlerName = handlerType.Name,
                Options = options,
                Scope = subscription?.Scope ?? SubscriptionScope.Service,
                SubscriptionName = subscription?.Name,
                Invoke = invoke
            };
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
        var subscription = options as SubscriptionOptions;
        return new MessageHandlerRegistration
        {
            MessageType = typeof(T),
            Kind = kind,
            HandlerName = $"{typeof(T).Name} delegate",
            Options = options,
            Scope = subscription?.Scope ?? SubscriptionScope.Service,
            SubscriptionName = subscription?.Name,
            Invoke = (_, message, context, cancellationToken) => handler((T)message, context, cancellationToken)
        };
    }
}
