using System;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>Sees what the client sends and how each delivery ends. Used by <c>FoundatioTestHarness</c>.</summary>
internal interface IMessagingObserver
{
    /// <summary>Called after the transport accepted <paramref name="outgoing"/>, created from <paramref name="message"/>.</summary>
    void OnSent(MessageDestination destination, object message, OutgoingMessage outgoing);

    /// <summary>
    /// Called after a delivery was settled. <paramref name="outcome"/> is the metrics outcome: success, retry,
    /// dead-lettered, skipped, expired, cancelled or lease-lost.
    /// </summary>
    void OnHandled(MessageSource source, IncomingMessage message, object? payload, string outcome, string? reason, Exception? error);
}
