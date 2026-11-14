using System;

namespace Foundatio.Messaging;

/// <summary>Information about the message being handled, and ways to decide its outcome without throwing.</summary>
/// <remarks>
/// <see cref="IHandlerContext.Attempt"/> is the delivery attempt, <see cref="IHandlerContext.CorrelationId"/> the
/// <see cref="DeliveryOptions.CorrelationId"/> the message was sent with, and <see cref="IHandlerContext.Headers"/> every
/// header, including <see cref="KnownHeaders"/>. <see cref="IHandlerContext.ReportProgressAsync"/> updates the monitor,
/// adds an event to the handler's trace span and logs at Debug; it never calls the transport, and the message's lease
/// is renewed whether or not progress is reported.
/// </remarks>
public interface IMessageContext : IHandlerContext
{
    /// <summary>The id the message was sent with: <see cref="DeliveryOptions.MessageId"/>, or a generated one.</summary>
    string MessageId { get; }

    /// <summary>The stable type name the message was sent with.</summary>
    string MessageType { get; }

    /// <summary>When the message was first sent, or null when that is unknown.</summary>
    DateTimeOffset? SentAt { get; }

    /// <summary>The <see cref="DeliveryOptions.GroupKey"/> the message was sent with, or null.</summary>
    string? GroupKey { get; }

    /// <summary>Where the message was received from, for example <c>queue:orders</c>.</summary>
    string Source { get; }

    /// <summary>The DI scope created for this handler invocation. Handler classes should prefer constructor injection.</summary>
    IServiceProvider Services { get; }

    /// <summary>When the handler returns, move the message to the dead-letter queue instead of completing it.</summary>
    void DeadLetter(string reason);

    /// <summary>When the handler returns, deliver the message again after <paramref name="delay"/> (or the retry policy's delay).</summary>
    /// <remarks>This counts as an attempt; once attempts run out the message is dead-lettered.</remarks>
    void Retry(TimeSpan? delay = null);
}
