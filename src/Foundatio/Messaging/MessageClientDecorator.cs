using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// An <see cref="IMessageClient"/> that passes every call to another one. Derive from it to change one call, for
/// example to fail a publish in a test, and override only that member.
/// </summary>
/// <example>
/// <code>
/// sealed class FailFirstPublish(IMessageClient inner) : MessageClientDecorator(inner)
/// {
///     private int _publishes;
///
///     public override Task&lt;string&gt; PublishAsync&lt;T&gt;(T message, PublishOptions? options = null, CancellationToken cancellationToken = default)
///     {
///         if (Interlocked.Increment(ref _publishes) == 1)
///             throw new InvalidOperationException("Simulated failure");
///
///         return base.PublishAsync(message, options, cancellationToken);
///     }
/// }
/// </code>
/// </example>
public abstract class MessageClientDecorator : IMessageClient
{
    /// <param name="inner">The client every call goes to unless overridden.</param>
    protected MessageClientDecorator(IMessageClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The client every call goes to unless overridden.</summary>
    protected IMessageClient Inner { get; }

    /// <inheritdoc />
    public virtual Task<string> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => Inner.SendAsync(message, options, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, Func<T, MessageKeys>? keys, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => Inner.SendBatchAsync(messages, keys, options, cancellationToken);

    /// <inheritdoc />
    public virtual Task<string> PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => Inner.PublishAsync(message, options, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, Func<T, MessageKeys>? keys, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => Inner.PublishBatchAsync(messages, keys, options, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, IMessageContext, CancellationToken, Task> handler, SubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => Inner.SubscribeAsync(handler, options, cancellationToken);
}
