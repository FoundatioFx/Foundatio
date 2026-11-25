using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>The receiver a <see cref="FakeServiceBusQueueClient"/> creates.</summary>
internal sealed class FakeServiceBusQueueReceiver(FakeServiceBusQueueClient client) : ServiceBusReceiver
{
    private static readonly TimeSpan EmptyPollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>Returns what the queue has, or waits up to <paramref name="maxWaitTime"/> for something to arrive.</summary>
    public override async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (maxWaitTime ?? TimeSpan.FromMinutes(1));
        client.StartWaiting();
        try
        {
            while (true)
            {
                var messages = client.Take(maxMessages);
                if (messages.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                    return messages;

                await Task.Delay(EmptyPollInterval, cancellationToken);
            }
        }
        finally
        {
            client.StopWaiting();
        }
    }

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default)
    {
        client.Abandon(message);
        return Task.CompletedTask;
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
