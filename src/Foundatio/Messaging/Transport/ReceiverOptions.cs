using System;

namespace Foundatio.Messaging.Transport;

/// <summary>Settings for a receiver.</summary>
public sealed record ReceiverOptions
{
    /// <summary>
    /// How long a received message stays leased to the receiver before it is delivered again. A broker that decides the
    /// lease itself reports it in <see cref="IncomingMessage.LeaseExpiresAt"/> instead.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A hint for push-based brokers: how many messages to buffer ahead of <see cref="IMessageReceiver.ReceiveAsync"/>.</summary>
    public int PrefetchCount { get; init; } = 16;
}
