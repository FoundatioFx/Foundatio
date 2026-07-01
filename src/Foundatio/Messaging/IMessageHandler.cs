using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// Handles messages of type <typeparamref name="T"/>. Register with
/// <c>AddFoundatio().Messaging.AddHandler&lt;T, THandler&gt;()</c> — registration carries no topology decision; the
/// caller's verb on <see cref="IMessageBus"/> decides delivery (<c>SendAsync</c> = one handler instance across the
/// fleet, <c>PublishAsync</c> = once per subscribing service, or every instance with
/// <see cref="MessageSubscriptionOptions.PerInstance"/>). A hosted service starts and dispatches to it. Handlers are
/// resolved from DI in their own scope per message, so they can inject scoped dependencies. Throwing from
/// <see cref="HandleAsync"/> triggers the core's retry/dead-letter policy.
/// </summary>
public interface IMessageHandler<T> where T : class
{
    Task HandleAsync(IReceivedMessage<T> message, CancellationToken cancellationToken);
}
