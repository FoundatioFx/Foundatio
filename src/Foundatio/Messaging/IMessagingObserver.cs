using System;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>Sees what the client sends and how each delivery ends. Used by <c>FoundatioTestHarness</c>.</summary>
internal interface IMessagingObserver
{
    /// <summary>Called after the transport accepted <paramref name="outgoing"/>, created from <paramref name="message"/>.</summary>
    void OnSent(MessageDestination destination, object message, OutgoingMessage outgoing);

    /// <summary>
    /// Called after the transport accepted a legacy <c>IMessageBus</c> publish to the legacy bus's own topic, for a
    /// <paramref name="messageType"/> it also sends to that type's topic (reported with <see cref="OnSent"/>).
    /// </summary>
    void OnLegacySent(MessageDestination destination, object message, OutgoingMessage outgoing, string messageType)
    {
    }

    /// <summary>
    /// Called after a delivery was settled. <paramref name="outcome"/> is the metrics outcome: success, retry,
    /// dead-lettered, skipped, expired, cancelled or lease-lost. <paramref name="context"/> is the handlers' context,
    /// or null when no handler ran.
    /// </summary>
    void OnHandled(MessageSource source, IncomingMessage message, object? payload, string outcome, string? reason, Exception? error, MessageContext? context = null);

    /// <summary>Called when <paramref name="handler"/> returned without throwing for the delivery <paramref name="context"/> belongs to, before it is settled.</summary>
    void OnHandlerCompleted(MessageContext context, MessageHandlerRegistration handler)
    {
    }
}
