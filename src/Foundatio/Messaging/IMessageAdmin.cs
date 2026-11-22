using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>
/// Operations for people rather than handlers: count a source's messages, inspect and replay dead letters, and remove
/// queues or subscriptions that are no longer used. Works on every transport.
/// </summary>
/// <example>
/// <code>
/// var source = admin.GetSubscription&lt;OrderPlaced&gt;();   // this service's subscription to the event
/// var failed = await admin.PeekDeadLettersAsync(source);
/// int replayed = await admin.ReplayDeadLettersAsync(source);
/// </code>
/// </example>
public interface IMessageAdmin
{
    /// <summary>
    /// The queues and durable subscriptions this app's registered handlers receive from, named as the transport sees
    /// them. Instance subscriptions are left out: they have no dead letters, and the transport names them.
    /// </summary>
    IReadOnlyList<MessageSource> GetSources();

    /// <summary>The queue messages of type <typeparamref name="T"/> are sent to, following this app's routing (<c>MapMessage</c> and <see cref="MessageAttribute"/>).</summary>
    MessageSource GetQueue<T>() where T : class;

    /// <summary>
    /// The durable subscription named <paramref name="name"/> to the topic messages of type <typeparamref name="T"/>
    /// are published to; by default the subscription of this service, which <c>AddSubscriber</c> uses unless its
    /// options name another.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> is null and no service name is set, on a transport that needs one.
    /// </exception>
    MessageSource GetSubscription<T>(string? name = null) where T : class;

    /// <summary>
    /// Counts the messages of a queue or durable subscription that are queued, delayed, in flight and dead-lettered, as
    /// the transport reports them. The counts are approximate and may lag sends and settlements by a few seconds; each is
    /// null when the transport cannot tell. Returns null when the transport cannot count the source at all, as with
    /// instance subscriptions on most transports. A source that does not exist yet counts as empty.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> is a dead-letter source; read <see cref="MessageSourceStats.DeadLettered"/> of the source it belongs to.
    /// </exception>
    Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default);

    /// <summary>Returns up to <paramref name="max"/> dead letters of a queue or durable subscription, leaving them in place.</summary>
    Task<IReadOnlyList<DeadLetterMessage>> PeekDeadLettersAsync(MessageSource source, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="max"/> dead letters of a queue or durable subscription after the first
    /// <paramref name="skip"/>, leaving them in place: a page of them. Peeking receives <paramref name="skip"/> plus
    /// <paramref name="max"/> messages and releases them, so later pages cost more.
    /// </summary>
    /// <remarks>
    /// Pages follow the dead-letter queue's order, which every transport but SQS keeps between calls; on SQS, whose
    /// order is approximate, pages may overlap or miss messages. A transport that limits how many messages one receiver
    /// holds ends the page there (Kafka: 1,000).
    /// </remarks>
    Task<IReadOnlyList<DeadLetterMessage>> PeekDeadLettersAsync(MessageSource source, int skip, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves up to <paramref name="max"/> dead letters back to the queue or subscription they came from, where they
    /// are handled again as new deliveries. A replayed subscription message goes only to that subscription.
    /// </summary>
    /// <returns>The number of messages replayed.</returns>
    Task<int> ReplayDeadLettersAsync(MessageSource source, int max = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the dead letter with id <paramref name="messageId"/> back to the queue or subscription it came from, like
    /// <see cref="ReplayDeadLettersAsync"/>. Looks through up to 10,000 dead letters for it, holding the others until
    /// it returns. If the dead-letter queue holds the message twice, only the first copy is replayed.
    /// </summary>
    /// <returns>Whether a dead letter with that id was found and replayed.</returns>
    /// <exception cref="MessageBusException">
    /// The first 10,000 dead letters do not include it, or the transport removes messages only in order
    /// (<see cref="TransportCapabilities.InOrderCompletion"/>: Kafka).
    /// </exception>
    Task<bool> ReplayDeadLetterAsync(MessageSource source, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the dead letter with id <paramref name="messageId"/>, such as a poison message that should never run.
    /// Looks through up to 10,000 dead letters for it, holding the others until it returns. If the dead-letter queue
    /// holds the message twice, only the first copy is deleted.
    /// </summary>
    /// <returns>Whether a dead letter with that id was found and deleted.</returns>
    /// <exception cref="MessageBusException">
    /// The first 10,000 dead letters do not include it, or the transport removes messages only in order
    /// (<see cref="TransportCapabilities.InOrderCompletion"/>: Kafka).
    /// </exception>
    Task<bool> DeleteDeadLetterAsync(MessageSource source, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every dead letter of a queue or durable subscription.</summary>
    Task PurgeDeadLettersAsync(MessageSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a queue or durable subscription with its pending messages and dead letters, for example the
    /// subscription of a service that was removed. Stop its handlers first; handling it again recreates it.
    /// </summary>
    Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default);
}
