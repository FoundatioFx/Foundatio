using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>Overloads of <see cref="IMessageClient"/> that call its members, so a decorator does not implement them.</summary>
public static class MessageClientExtensions
{
    /// <summary>Sends several messages efficiently, with generated ids. Each is routed by its own type.</summary>
    /// <returns>The generated message ids, in the order of <paramref name="messages"/>.</returns>
    public static Task<IReadOnlyList<string>> SendBatchAsync<T>(this IMessageClient client, IEnumerable<T> messages, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        return client.SendBatchAsync(messages, null, options, cancellationToken);
    }

    /// <summary>Publishes several events efficiently, with generated ids. Each is routed by its own type.</summary>
    /// <returns>The generated message ids, in the order of <paramref name="messages"/>.</returns>
    public static Task<IReadOnlyList<string>> PublishBatchAsync<T>(this IMessageClient client, IEnumerable<T> messages, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        return client.PublishBatchAsync(messages, null, options, cancellationToken);
    }
}
