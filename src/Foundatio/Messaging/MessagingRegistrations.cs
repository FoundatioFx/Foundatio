using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>Everything added by every <c>AddFoundatioMessaging</c> call, read when the host starts.</summary>
internal sealed class MessagingRegistrations
{
    public List<MessageHandlerRegistration> Handlers { get; } = [];
    public List<Action<MessagingOptions>> ConfigureOptions { get; } = [];
    public List<(Type Type, string Name, string? Queue, string? Topic)> Maps { get; } = [];
}
