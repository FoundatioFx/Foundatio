using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>
/// Operations for people rather than handlers: inspect and replay dead letters, and remove queues or
/// subscriptions that are no longer used. Works on every transport.
/// </summary>
/// <example>
/// <code>
/// var source = MessageSource.Subscription("orders.order-placed", "billing");
/// var failed = await admin.PeekDeadLettersAsync(source);
/// int replayed = await admin.ReplayDeadLettersAsync(source);
/// </code>
/// </example>
public interface IMessageAdmin
{
    /// <summary>Returns up to <paramref name="max"/> dead letters of a queue or durable subscription, leaving them in place.</summary>
    Task<IReadOnlyList<DeadLetterMessage>> PeekDeadLettersAsync(MessageSource source, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves up to <paramref name="max"/> dead letters back to the queue or subscription they came from, where they
    /// are handled again as new deliveries. A replayed subscription message goes only to that subscription.
    /// </summary>
    /// <returns>The number of messages replayed.</returns>
    Task<int> ReplayDeadLettersAsync(MessageSource source, int max = 100, CancellationToken cancellationToken = default);

    /// <summary>Deletes every dead letter of a queue or durable subscription.</summary>
    Task PurgeDeadLettersAsync(MessageSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a queue or durable subscription with its pending messages and dead letters, for example the
    /// subscription of a service that was removed. Stop its handlers first; handling it again recreates it.
    /// </summary>
    Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default);
}
