using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Foundatio.Messaging.Benchmarks.Targets;

/// <summary>
/// What a careful hand-written consumer does on a pull broker: a few receive loops keep up to twice the handler count
/// in flight, the handlers read from a channel, and acknowledgements are group-committed in batches. The floor the
/// Foundatio paths are compared with.
/// </summary>
public sealed class RawPump<T>(
    Func<int, CancellationToken, Task<IReadOnlyList<T>>> receive,
    Func<T, BenchMessage> decode,
    Func<IReadOnlyList<T>, Task> acknowledge,
    int maxBatch,
    int receivers) : IAsyncDisposable
{
    private readonly Channel<T> _received = Channel.CreateUnbounded<T>();
    private readonly Channel<T> _handled = Channel.CreateUnbounded<T>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _tasks = [];
    private int _inFlight;

    public void Start(int concurrency, Func<BenchMessage, ValueTask> handler)
    {
        int limit = Math.Max(2 * concurrency, maxBatch);
        for (int i = 0; i < receivers; i++)
            _tasks.Add(Task.Run(() => ReceiveLoopAsync(limit)));
        for (int i = 0; i < concurrency; i++)
            _tasks.Add(Task.Run(() => HandleLoopAsync(handler)));
        _tasks.Add(Task.Run(AcknowledgeLoopAsync));
    }

    private async Task ReceiveLoopAsync(int limit)
    {
        var token = _stop.Token;
        int idle = 0;
        while (!token.IsCancellationRequested)
        {
            int room = Math.Min(maxBatch, limit - Volatile.Read(ref _inFlight));
            if (room <= 0)
            {
                await Task.Delay(1, token).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<T> items;
            try
            {
                items = await receive(room, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"raw receive failed: {ex.Message}");
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }

            if (items.Count == 0)
            {
                // brokers without a blocking receive: back off up to 20 ms, as the transports' own polling does
                idle = Math.Min(idle + 1, 20);
                await Task.Delay(idle, token).ConfigureAwait(false);
                continue;
            }

            idle = 0;
            Interlocked.Add(ref _inFlight, items.Count);
            foreach (var item in items)
                _received.Writer.TryWrite(item);
        }
    }

    private async Task HandleLoopAsync(Func<BenchMessage, ValueTask> handler)
    {
        try
        {
            await foreach (var item in _received.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await handler(decode(item)).ConfigureAwait(false);
                _handled.Writer.TryWrite(item);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AcknowledgeLoopAsync()
    {
        var batch = new List<T>(maxBatch);
        try
        {
            while (await _handled.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (batch.Count < maxBatch && _handled.Reader.TryRead(out var item))
                    batch.Add(item);

                await acknowledge(batch.ToList()).ConfigureAwait(false);
                Interlocked.Add(ref _inFlight, -batch.Count);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Task.WhenAll(_tasks);
        }
        catch (OperationCanceledException) { }

        _stop.Dispose();
    }
}
