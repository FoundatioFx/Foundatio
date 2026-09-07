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
        _worker = Task.Run(RunAsync);
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
                var batch = await ReadBatchAsync(_delayWhenIdle || executing.Count > 0).ConfigureAwait(false);
                if (batch.Count > 0)
                    executing.Add(ExecuteBatchAsync(batch));
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(_delay);
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
                else if (!waitForMore || _delay == TimeSpan.Zero || !await _channel.Reader.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
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
            var results = await _execute(values, timeout.Token).ConfigureAwait(false);
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
