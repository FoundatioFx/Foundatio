using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging;

/// <summary>A directly received delivery. Disposal returns unsettled work for redelivery and stops lease renewal.</summary>
public interface IReceivedMessage : IMessageContext, IAsyncDisposable;

/// <summary>A directly received typed delivery. Use await using, then complete or reject it explicitly.</summary>
public interface IReceivedMessage<out T> : IReceivedMessage, IMessageContext<T> where T : class;

/// <summary>Options for receiving one queued message without registering a handler.</summary>
public sealed record MessageReceiveOptions
{
    /// <summary>Queue name. Null uses the message type's configured route.</summary>
    public string? Destination { get; init; }

    /// <summary>Maximum wait for a message. Zero checks for immediately available work.</summary>
    public TimeSpan WaitTime { get; init; }
}

internal class ReceivedMessage(IMessageContext context, CancellationTokenSource cancellation, Task<bool> supervision, Func<CancellationToken, Task> abandon) : IReceivedMessage
{
    private int _disposed;
    public string Id => context.Id;
    public string BrokerMessageId => context.BrokerMessageId;
    public ReadOnlyMemory<byte> Body => context.Body;
    public MessageHeaders Headers => context.Headers;
    public string? CorrelationId => context.CorrelationId;
    public string? MessageType => context.MessageType;
    public MessagePriority Priority => context.Priority;
    public int Attempts => context.Attempts;
    public bool IsHandled => context.IsHandled;
    public CancellationToken CancellationToken => context.CancellationToken;

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await context.CompleteAsync(cancellationToken).AnyContext();
        await DisposeAsync().AnyContext();
    }

    public async Task RejectAsync(RejectOptions? options = null, CancellationToken cancellationToken = default)
    {
        await context.RejectAsync(options, cancellationToken).AnyContext();
        await DisposeAsync().AnyContext();
    }

    public Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
        => context.RenewLockAsync(duration, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await cancellation.CancelAsync().AnyContext();
            bool leaseLost = await supervision.AnyContext();
            if (!context.IsHandled && !leaseLost)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await abandon(cleanup.Token).AnyContext();
            }
        }
        finally
        {
            await cancellation.CancelAsync().AnyContext();
            await supervision.AnyContext();
            cancellation.Dispose();
        }
    }
}

internal sealed class ReceivedMessage<T>(IMessageContext<T> context, CancellationTokenSource cancellation, Task<bool> supervision, Func<CancellationToken, Task> abandon)
    : ReceivedMessage(context, cancellation, supervision, abandon), IReceivedMessage<T> where T : class
{
    public T Message => context.Message;
}
