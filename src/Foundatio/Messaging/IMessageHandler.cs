using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// Handles one message in a DI scope. Register with AddConsumer for queued work or AddSubscriber for published events.
/// Throw to apply the endpoint retry policy; return to acknowledge successfully processed messages.
/// </summary>
public interface IMessageHandler<T> where T : class
{
    Task HandleAsync(IMessageContext<T> context, CancellationToken cancellationToken);
}
