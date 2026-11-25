using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Messaging;

/// <summary>Options for the <see cref="IMessageBus"/> that <c>AddLegacyMessageBus()</c> provides.</summary>
public class LegacyMessageBusOptions : SharedMessageBusOptions
{
    /// <summary>
    /// Message types that have moved to <see cref="IMessageClient"/>, for publishers that still use <see cref="IMessageBus"/>,
    /// such as a library you can't change. A legacy publish of one of these types (not of types derived from them) is
    /// sent once, to the topic <see cref="IMessageClient.PublishAsync{T}"/> sends it to, as if it were published that
    /// way, instead of to both that topic and the legacy topic.
    /// </summary>
    /// <remarks>
    /// <see cref="IMessageSubscriber.SubscribeAsync{T}"/> subscribers of these types, of their base types and of
    /// <see cref="IMessage"/> keep receiving them, each once, from that topic. List the types the same way in every
    /// process that shares the bus: another process's legacy subscriber of the type still receives it when it knows the
    /// type (mapped, marked with <see cref="MessageAttribute"/>, or handled there), but its subscribers of a base type or
    /// of <see cref="IMessage"/> do not.
    /// </remarks>
    [DisallowNull]
    public HashSet<Type> MigratedMessageTypes { get => field; set => field = value ?? []; } = [];
}
