using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// Handles messages of type <typeparamref name="T"/> received from a queue or a pub/sub subscription. Register a
/// handler with <c>AddFoundatio().Messaging.AddQueueHandler&lt;T, THandler&gt;()</c> (competing consumers) or
/// <c>AddBroadcastHandler&lt;T, THandler&gt;()</c> (fan-out); a hosted service then starts and dispatches to it.
/// Handlers are resolved from DI in their own scope per message, so they can inject scoped dependencies. Throwing from
/// <see cref="HandleAsync"/> triggers the core's retry/dead-letter policy.
/// </summary>
public interface IMessageHandler<T> where T : class
{
    Task HandleAsync(IReceivedMessage<T> message, CancellationToken cancellationToken);
}
