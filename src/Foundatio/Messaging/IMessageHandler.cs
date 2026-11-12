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
    /// <summary>Handles one delivery of <paramref name="message"/>.</summary>
    /// <param name="message">The deserialized message.</param>
    /// <param name="context">The delivery's metadata, and a way to dead-letter or retry it without throwing.</param>
    /// <param name="cancellationToken">Cancelled when shutdown outlasts <see cref="MessagingOptions.ShutdownTimeout"/> or the delivery's lease is lost; the message is then delivered again.</param>
    Task HandleAsync(T message, IMessageContext context, CancellationToken cancellationToken);
}
