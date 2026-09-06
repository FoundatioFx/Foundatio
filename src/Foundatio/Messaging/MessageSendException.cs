using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>The known outcome of one message in a failed send.</summary>
public enum MessageSendStatus
{
    /// <summary>No send or scheduling operation was attempted.</summary>
    NotAttempted,
    /// <summary>The transport or scheduling store confirmed acceptance.</summary>
    Accepted,
    /// <summary>The operation failed without confirming whether the message was accepted. Retrying may duplicate delivery.</summary>
    Unknown
}

/// <summary>An application message ID and its send outcome.</summary>
public sealed record MessageSendOutcome(string MessageId, MessageSendStatus Status);

/// <summary>
/// A send failed. Outcomes cover every input message in input order, including messages not attempted.
/// Retry with the same application IDs and deduplicate at the consumer; sends are not transactions.
/// </summary>
public sealed class MessageSendException : MessageBusException
{
    public MessageSendException(IReadOnlyList<MessageSendOutcome> outcomes, Exception innerException)
        : base("Message sending failed. Inspect Outcomes before retrying; messages with an unknown outcome may already have been accepted.", innerException)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        Outcomes = outcomes;
    }

    public IReadOnlyList<MessageSendOutcome> Outcomes { get; }
}

/// <summary>
/// A sequential transport batch failed after accepting a prefix. The next message has an unknown outcome;
/// later messages were not attempted. Providers sending concurrently must report a general exception instead.
/// </summary>
public sealed class TransportSendException : MessageBusException
{
    public TransportSendException(int acceptedCount, Exception innerException)
        : base("The transport accepted part of a batch before sending failed.", innerException)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(acceptedCount);
        AcceptedCount = acceptedCount;
    }

    public int AcceptedCount { get; }
}
