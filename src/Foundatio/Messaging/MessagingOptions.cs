using System;
using Foundatio.Serializer;

namespace Foundatio.Messaging;

/// <summary>Settings shared by all handlers.</summary>
public sealed class MessagingOptions
{
    /// <summary>Names durable subscriptions. Defaults to the host's application name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Used when a handler does not set its own policy.</summary>
    public RetryPolicy DefaultRetryPolicy { get; set; } = RetryPolicy.Default;

    /// <summary>How long shutdown waits for in-flight messages before cancelling them. They are then delivered again.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>Defaults to the registered <see cref="ISerializer"/>, or JSON.</summary>
    public ISerializer? Serializer { get; set; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(DefaultRetryPolicy);
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero);
    }
}
