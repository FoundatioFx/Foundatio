using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// One declarative message-handler registration: a description for logging and a factory that starts the underlying
/// queue consumer or pub/sub subscription and returns it for disposal on shutdown. Built by the consumer/subscriber
/// builder methods, which bind the message type at compile time (one registration per delivery verb).
/// </summary>
internal sealed class MessageHandlerRegistration
{
    public required string Description { get; init; }
    public required Func<IServiceProvider, CancellationToken, Task<IAsyncDisposable>> StartAsync { get; init; }
}

/// <summary>The DI-selected <see cref="TopologyMode"/>, applied at startup and by the message clients on use.</summary>
/// <summary>The effective topology policy selected for this messaging client.</summary>
public sealed record MessagingTopologyOptions(TopologyMode Mode);
