using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

public static class MessageExecutionStoreExtensions
{
    /// <summary>Observes a tracked execution until it reaches a terminal state. Cancellation stops observation, not the queued work.</summary>
    public static async Task<MessageExecutionState> WaitForCompletionAsync(this IMessageExecutionStore store, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await store.GetJobStateAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Execution '{id}' was not found or has expired.");
            if (state.Status is MessageExecutionStatus.Completed or MessageExecutionStatus.Failed or MessageExecutionStatus.Cancelled)
                return state;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }
}
