using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads a source's counts: ready messages from passive declares on a channel of its own (a declare of a queue that
/// does not exist closes the channel it was sent on), or, with a management endpoint, ready and unacknowledged
/// messages from the management API, whose details of the source's queue it also hands to the delay inspector.
/// </summary>
internal sealed class RabbitMQSourceStatsReader : IAsyncDisposable
{
    private readonly RabbitMQChannelSlot _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly RabbitMQManagementClient? _management;
    private readonly RabbitMQDelayInspector? _inspector;

    public RabbitMQSourceStatsReader(RabbitMQMessageTransport transport, RabbitMQManagementClient? management, RabbitMQDelayInspector? inspector)
    {
        _channel = new RabbitMQChannelSlot(async ct => await (await transport.GetConnectionAsync(consumer: false, ct).AnyContext()).CreateChannelAsync(cancellationToken: ct).AnyContext());
        _management = management;
        _inspector = inspector;
    }

    public async Task<MessageSourceStats?> GetAsync(MessageSource source, string queue, string deadLetterQueue, CancellationToken cancellationToken)
    {
        if (_management is not null)
        {
            var counts = await Task.WhenAll(GetCountsAsync(queue, inspect: true, cancellationToken), GetCountsAsync(deadLetterQueue, inspect: false, cancellationToken)).AnyContext();
            return new MessageSourceStats
            {
                Source = source,
                Queued = counts[0].Ready,
                InFlight = counts[0].Unacknowledged,
                DeadLettered = counts[1].Ready + counts[1].Unacknowledged
            };
        }

        await _lock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            return new MessageSourceStats
            {
                Source = source,
                Queued = await CountReadyAsync(queue, cancellationToken).AnyContext(),
                DeadLettered = await CountReadyAsync(deadLetterQueue, cancellationToken).AnyContext()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask DisposeAsync() => _channel.DisposeAsync();

    private async Task<long> CountReadyAsync(string queue, CancellationToken cancellationToken)
    {
        try
        {
            var channel = await _channel.GetAsync(cancellationToken).AnyContext();
            return (await channel.QueueDeclarePassiveAsync(queue, cancellationToken).AnyContext()).MessageCount;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return 0;
        }
        catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException)
        {
            throw new MessageBusException($"Could not count the messages in {queue}: {ex.Message}", ex);
        }
    }

    private async Task<(long Ready, long Unacknowledged)> GetCountsAsync(string queue, bool inspect, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await _management!.GetQueueAsync(queue, cancellationToken).AnyContext();
            if (document is null)
                return (0, 0);

            if (inspect)
                _inspector?.InspectTarget(queue, document.RootElement);

            // A queue that was just declared has no statistics until the broker's next collection.
            return (GetCount(document.RootElement, "messages_ready"), GetCount(document.RootElement, "messages_unacknowledged"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MessageBusException($"Could not read the counts of {queue} from the RabbitMQ management API: {ex.Message}", ex);
        }
    }

    private static long GetCount(JsonElement queue, string property)
    {
        return queue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) ? count : 0;
    }
}
