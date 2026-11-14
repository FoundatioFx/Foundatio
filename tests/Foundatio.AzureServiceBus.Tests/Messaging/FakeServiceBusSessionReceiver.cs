using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>The receiver a <see cref="FakeServiceBusClient"/> accept returns, holding one <see cref="FakeServiceBusSession"/> until it is closed.</summary>
internal sealed class FakeServiceBusSessionReceiver : ServiceBusSessionReceiver
{
    private static readonly TimeSpan EmptyPollInterval = TimeSpan.FromMilliseconds(5);

    private readonly FakeServiceBusClient _client;
    private readonly FakeServiceBusSession _session;
    private DateTimeOffset _lockedUntil;

    public FakeServiceBusSessionReceiver(FakeServiceBusClient client, FakeServiceBusSession session, DateTimeOffset lockedUntil)
    {
        _client = client;
        _session = session;
        _lockedUntil = lockedUntil;
    }

    public override string SessionId => _session.SessionId;

    public override DateTimeOffset SessionLockedUntil => _lockedUntil;

    /// <summary>Returns what the session has after <see cref="FakeServiceBusClient.ReceiveLatency"/>, or waits up to <paramref name="maxWaitTime"/> for something to arrive.</summary>
    public override async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
    {
        if (_client.ReceiveLatency > TimeSpan.Zero)
            await Task.Delay(_client.ReceiveLatency, cancellationToken);

        var deadline = DateTimeOffset.UtcNow + (maxWaitTime ?? TimeSpan.FromMinutes(1));
        while (true)
        {
            var messages = _session.Take(this, maxMessages);
            if (messages.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                return messages;

            await Task.Delay(EmptyPollInterval, cancellationToken);
        }
    }

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        _session.Complete(this, message);
        return Task.CompletedTask;
    }

    public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default)
    {
        _session.Abandon(this, message, propertiesToModify);
        return Task.CompletedTask;
    }

    public override Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string? deadLetterErrorDescription = null, CancellationToken cancellationToken = default)
    {
        _session.Complete(this, message);
        return Task.CompletedTask;
    }

    public override async Task RenewSessionLockAsync(CancellationToken cancellationToken = default)
    {
        _client.CountRenewal();
        if (_client.HangRenewals)
            await Task.Delay(Timeout.Infinite, cancellationToken);

        _lockedUntil = _session.Renew(this);
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default)
    {
        _session.Close(this);
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync() => await CloseAsync();
}
