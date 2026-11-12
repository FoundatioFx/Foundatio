using System.Threading;
using System.Threading.Tasks;
using Foundatio.Resilience;

namespace Foundatio.Messaging;

/// <summary>Handles messages of type <typeparamref name="T"/>. Resolved from a new DI scope for every message.</summary>
/// <remarks>
/// Return normally to complete the message. Throw to retry it according to the handler's <see cref="RetryPolicy"/>;
/// once attempts run out it is dead-lettered. Use <see cref="IMessageContext.DeadLetter"/> or
/// <see cref="IMessageContext.Retry"/> to decide without throwing.
/// </remarks>
public interface IMessageHandler<in T> where T : class
{
    Task HandleAsync(T message, IMessageContext context, CancellationToken cancellationToken);
}
