using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

internal sealed class AwsRequestBatcher<T, TResult> : IAsyncDisposable
{
    private readonly Channel<Pending> _channel;
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task<TResult[]>> _execute;
    private readonly Func<T, int> _size;
    private readonly int _maximumBytes;
    private readonly int _concurrency;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _timeout;
    private readonly bool _delayWhenIdle;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _disposed;
    private int _activeRequests;
    private int _observedBatchSize;

    public AwsRequestBatcher(AwsMessageTransportOptions options, int maximumBytes, Func<T, int> size,
        Func<IReadOnlyList<T>, CancellationToken, Task<TResult[]>> execute, bool delayWhenIdle = true)
    {
        _maximumBytes = maximumBytes;
        _size = size;
        _execute = execute;
        _delayWhenIdle = delayWhenIdle;
        _concurrency = options.MaxConcurrentBatches;
        _delay = options.BatchDelay;
        _timeout = options.BatchTimeout;
        _channel = Channel.CreateBounded<Pending>(new BoundedChannelOptions(options.MaxPendingBatchMessages)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        if (ExecutionContext.IsFlowSuppressed())
            _worker = Task.Run(RunAsync);
        else
        {
            using (ExecutionContext.SuppressFlow())
                _worker = Task.Run(RunAsync);
        }
    }

    public void ObserveBatchSize(int count)
    {
        count = Math.Clamp(count, 1, 10);
        int previous = Volatile.Read(ref _observedBatchSize);
        while (previous < count)
        {
            int observed = Interlocked.CompareExchange(ref _observedBatchSize, count, previous);
            if (observed == previous)
                break;
            previous = observed;
        }
    }

    public async Task<TResult> ExecuteAsync(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new Pending(value);
        using var registration = cancellationToken.UnsafeRegister(static (state, token) =>
            ((Pending)state!).Completion.TrySetCanceled(token), pending);
        try
        {
            await _channel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(AwsMessageTransport));
        }
    }

    private async Task RunAsync()
    {
        var executing = new List<Task>(_concurrency);
        int previousBatchSize = 0;
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                executing.RemoveAll(static task => task.IsCompleted);
                if (executing.Count >= _concurrency)
                {
                    await Task.WhenAny(executing).ConfigureAwait(false);
                    executing.RemoveAll(static task => task.IsCompleted);
                }
                var batch = await ReadBatchAsync(Volatile.Read(ref _activeRequests) > 0 || (_delayWhenIdle && previousBatchSize != 1)).ConfigureAwait(false);
                if (batch.Count > 0)
                {
                    previousBatchSize = batch.Count;
                    executing.Add(ExecuteBatchAsync(batch));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out var pending))
                pending.Completion.TrySetException(new ObjectDisposedException(nameof(AwsMessageTransport)));
            await Task.WhenAll(executing).ConfigureAwait(false);
        }
    }

    private async Task<List<Pending>> ReadBatchAsync(bool waitForMore)
    {
        var batch = new List<Pending>(10);
        int bytes = 0;
        Task? deadline = null;
        try
        {
            while (batch.Count < 10)
            {
                if (_channel.Reader.TryPeek(out var pending))
                {
                    if (pending.Completion.Task.IsCompleted)
                    {
                        _channel.Reader.TryRead(out _);
                        continue;
                    }
                    int size = _size(pending.Value);
                    if (batch.Count > 0 && bytes + size > _maximumBytes)
                        break;
                    _channel.Reader.TryRead(out _);
                    batch.Add(pending);
                    bytes += size;
                }
                else
                {
                    int observedBatchSize = Volatile.Read(ref _observedBatchSize);
                    if (!waitForMore || _delay == TimeSpan.Zero || (observedBatchSize > 0 && batch.Count >= observedBatchSize))
                        break;
                    deadline ??= Task.Delay(_delay, _stop.Token);
                    var available = _channel.Reader.WaitToReadAsync(_stop.Token).AsTask();
                    if (await Task.WhenAny(available, deadline).ConfigureAwait(false) != available || !await available.ConfigureAwait(false))
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        return batch;
    }

    private async Task ExecuteBatchAsync(List<Pending> batch)
    {
        batch.RemoveAll(static entry => entry.Completion.Task.IsCompleted);
        if (batch.Count == 0)
            return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(_timeout);
            var values = new T[batch.Count];
            for (int i = 0; i < batch.Count; i++)
                values[i] = batch[i].Value;
            TResult[] results;
            Interlocked.Increment(ref _activeRequests);
            try { results = await _execute(values, timeout.Token).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _activeRequests); }
            if (results.Length != batch.Count)
                throw new MessageBusException("AWS returned an incomplete batch result.");
            for (int i = 0; i < batch.Count; i++)
                batch[i].Completion.TrySetResult(results[i]);
        }
        catch (Exception ex)
        {
            foreach (var pending in batch)
                pending.Completion.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _channel.Writer.TryComplete();
        _stop.CancelAfter(_timeout);
        try { await _worker.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }

    private sealed class Pending(T value)
    {
        public T Value { get; } = value;
        public TaskCompletionSource<TResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
