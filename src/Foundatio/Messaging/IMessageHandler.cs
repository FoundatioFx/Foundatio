using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// Handles messages of type <typeparamref name="T"/>. Register with
/// <c>AddFoundatio().Messaging.AddHandler&lt;T, THandler&gt;()</c> — registration carries no topology decision; the
/// caller's verb on <see cref="IMessageBus"/> decides delivery (<c>SendAsync</c> = one handler instance across the
/// fleet, <c>PublishAsync</c> = once per subscribing service). A hosted service starts and dispatches to it. Handlers
/// are resolved from DI in their own scope per message, so they can inject scoped dependencies. Throwing from
/// <see cref="HandleAsync"/> triggers the core's retry/dead-letter policy.
/// </summary>
public interface IMessageHandler<T> where T : class
{
    Task HandleAsync(IReceivedMessage<T> message, CancellationToken cancellationToken);
}

/// <summary>
/// Options for a declaratively-registered message handler (<c>AddFoundatio().Messaging.AddHandler&lt;T, THandler&gt;(o =&gt; ...)</c>).
/// </summary>
public sealed class MessageHandlerOptions
{
    /// <summary>
    /// When true, published messages are received by EVERY running instance (each instance takes a unique
    /// subscription), instead of once per service. For per-instance local state — cache invalidation, config reload.
    /// Mutually exclusive with <see cref="Subscription"/>. Does not affect sent messages, which always go to exactly
    /// one instance.
    /// </summary>
    public bool PerInstance { get; set; }

    /// <summary>
    /// The subscriber-group identity used for published messages. Defaults to the service identity, so all instances
    /// of a service share one subscription and compete (each published message is handled once per service). Set an
    /// explicit name to form an independent named subscriber group.
    /// </summary>
    public string? Subscription { get; set; }

    /// <summary>Maximum messages this handler processes concurrently per instance. Default 1.</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Maximum delivery attempts before dead-lettering. Null uses the default <see cref="RetryPolicy"/>.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Delay before each redelivery given the 1-based attempt number. Null defers to the transport's timing.</summary>
    public Func<int, TimeSpan>? RedeliveryBackoff { get; set; }

    /// <summary>Whether messages auto-complete when the handler returns (default) or are settled manually.</summary>
    public AckMode AckMode { get; set; } = AckMode.Auto;
}
