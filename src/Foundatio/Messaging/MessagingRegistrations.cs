using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>Everything added by every <c>AddFoundatioMessaging</c> call, read when the host starts.</summary>
internal sealed class MessagingRegistrations
{
    public List<MessageHandlerRegistration> Handlers { get; } = [];
    public List<(Type Type, string Name, string? Queue, string? Topic)> Maps { get; } = [];

    /// <summary>Wrap the <see cref="IMessageClient"/> the service provider gives out, in the order added.</summary>
    public List<Func<IServiceProvider, IMessageClient, IMessageClient>> ClientDecorators { get; } = [];
}
