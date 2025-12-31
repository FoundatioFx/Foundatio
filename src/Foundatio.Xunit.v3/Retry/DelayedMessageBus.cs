#nullable enable

using System;
using System.Collections.Generic;
using Xunit.Sdk;
using Xunit.v3;

namespace Foundatio.Xunit;

/// <summary>
/// Used to capture messages to potentially be forwarded later. Messages are forwarded by
/// calling <see cref="Flush"/>.
/// </summary>
public class DelayedMessageBus : IMessageBus
{
    private readonly IMessageBus _innerBus;
    private readonly List<IMessageSinkMessage> _messages = [];
    private bool _disposed;

    public DelayedMessageBus(IMessageBus innerBus)
    {
        _innerBus = innerBus;
    }

    public bool QueueMessage(IMessageSinkMessage message)
    {
        lock (_messages)
        {
            if (_disposed)
                return true;

            _messages.Add(message);
        }

        // No way to ask the inner bus if they want to cancel without sending them the message, so
        // we just go ahead and continue always.
        return true;
    }

    /// <summary>
    /// Flushes all queued messages to the inner message bus.
    /// </summary>
    public void Flush()
    {
        List<IMessageSinkMessage> messagesToSend;

        lock (_messages)
        {
            if (_disposed)
                return;

            // Copy messages to a new list to avoid holding the lock while sending
            messagesToSend = [.. _messages];
            _messages.Clear();
        }

        // Send messages outside the lock to avoid potential deadlocks
        foreach (var message in messagesToSend)
            _innerBus.QueueMessage(message);
    }

    public void Dispose()
    {
        lock (_messages)
        {
            if (_disposed)
                return;

            _disposed = true;
            _messages.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
