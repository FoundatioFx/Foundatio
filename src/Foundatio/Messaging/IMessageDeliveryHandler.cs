using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>Decides what happens to each delivery a <see cref="MessageProcessor"/> receives.</summary>
internal interface IMessageDeliveryHandler
{
    /// <summary>
    /// Handles and settles one delivery. Returns true when it left the source (completed or dead-lettered).
    /// <paramref name="abortToken"/> is cancelled when shutdown stops waiting for handlers.
    /// </summary>
    Task<bool> HandleAsync(MessageDelivery delivery, CancellationToken abortToken);

    /// <summary>
    /// Called after a delivery was released without being handled: <c>held</c> behind an earlier message of its group,
    /// <c>cancelled</c> by shutdown, or <c>lease-lost</c> while it waited.
    /// </summary>
    void OnReleased(MessageDelivery delivery, string outcome);
}
