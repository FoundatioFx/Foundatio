using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging.Benchmarks;

[Message("bench.message")]
public sealed class BenchMessage
{
    /// <summary><see cref="Stopwatch.GetTimestamp"/> when the message was sent, for end-to-end latency.</summary>
    public long Sent { get; set; }
    public int Seq { get; set; }
    public string? Group { get; set; }
    public string Data { get; set; } = "";
}

/// <summary>One way of moving <see cref="BenchMessage"/>s through a broker: v2, the legacy Foundatio API, or the raw SDK.</summary>
public interface IBenchTarget : IAsyncDisposable
{
    /// <summary>Creates the queue or subscription, so that messages sent before <see cref="StartAsync"/> are kept.</summary>
    Task InitAsync(CancellationToken cancellationToken);

    Task SendAsync(BenchMessage message, CancellationToken cancellationToken);

    Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken);

    /// <summary>Starts handling with <paramref name="concurrency"/> concurrent handlers; returns once receiving has started.</summary>
    Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken);

    Task StopAsync();

    /// <summary>Removes what <see cref="InitAsync"/> created.</summary>
    Task CleanupAsync();
}

public abstract class BenchTargetBase(BenchOptions options) : IBenchTarget
{
    protected BenchOptions Options { get; } = options;

    public virtual Task InitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public abstract Task SendAsync(BenchMessage message, CancellationToken cancellationToken);

    public virtual async Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
            await SendAsync(message, cancellationToken);
    }

    public abstract Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken);

    public abstract Task StopAsync();

    public virtual Task CleanupAsync() => Task.CompletedTask;

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
