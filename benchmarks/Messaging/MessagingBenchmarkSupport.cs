using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Benchmarks.Messaging;

/// <summary>A message whose JSON is about <see cref="Create"/>'s size in bytes.</summary>
[Message("bench.payload")]
public sealed class BenchPayload
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Data { get; set; } = "";

    /// <summary>A payload whose JSON body is close to <paramref name="bytes"/> bytes.</summary>
    public static BenchPayload Create(int bytes)
    {
        const int overhead = 40; // {"Id":123456,"Name":"order","Data":""}
        return new BenchPayload { Id = 123456, Name = "order", Data = new string('x', Math.Max(0, bytes - overhead)) };
    }
}

/// <summary>Counts handled messages and completes once the expected number arrived.</summary>
public sealed class Countdown
{
    private int _remaining;
    private TaskCompletionSource _done = NewSignal();

    public Task Reset(int expected)
    {
        _done = NewSignal();
        Volatile.Write(ref _remaining, expected);
        return _done.Task;
    }

    public void Signal()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
            _done.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Starts and stops the hosted services of a service provider, without a full generic host.</summary>
public sealed class BenchHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<IHostedService> _hosted;

    private BenchHost(ServiceProvider services, List<IHostedService> hosted)
    {
        _services = services;
        _hosted = hosted;
    }

    public IServiceProvider Services => _services;

    public static async Task<BenchHost> StartAsync(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        configure(services);
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        return new BenchHost(provider, hosted);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var service in Enumerable.Reverse(_hosted))
            await service.StopAsync(timeout.Token);

        await _services.DisposeAsync();
    }
}

/// <summary>
/// The floor: what any in-process queue has to do at least. One unbounded channel of serialized bodies, read by
/// <c>concurrency</c> loops that deserialize each body and call the handler.
/// </summary>
public sealed class ChannelFloor : IAsyncDisposable
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly Task[] _readers;

    public ChannelFloor(int concurrency, Action<BenchPayload> handler)
    {
        _readers = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            _readers[i] = Task.Run(async () =>
            {
                var reader = _channel.Reader;
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out byte[] body))
                        handler(JsonSerializer.Deserialize<BenchPayload>(body)!);
                }
            });
        }
    }

    public void Send(BenchPayload payload) => _channel.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(payload));

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await Task.WhenAll(_readers).ConfigureAwait(false);
    }
}

/// <summary>A legacy <see cref="QueueJobBase{T}"/> that calls a handler for each entry.</summary>
public sealed class LegacyBenchQueueJob : QueueJobBase<BenchPayload>
{
    private readonly Func<IQueueEntry<BenchPayload>, JobResult> _handler;

    public LegacyBenchQueueJob(IQueue<BenchPayload> queue, Func<IQueueEntry<BenchPayload>, JobResult> handler) : base(queue, loggerFactory: NullLoggerFactory.Instance)
    {
        _handler = handler;
    }

    protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<BenchPayload> context) => Task.FromResult(_handler(context.QueueEntry));
}

/// <summary>Runs <c>concurrency</c> loops of a legacy queue job, as <c>JobRunner</c> does with several instances.</summary>
public sealed class LegacyQueueWorkers : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _loops;

    public LegacyQueueWorkers(InMemoryQueue<BenchPayload> queue, int concurrency, Func<IQueueEntry<BenchPayload>, JobResult> handler)
    {
        var job = new LegacyBenchQueueJob(queue, handler);
        _loops = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            _loops[i] = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                    await job.RunAsync(_stop.Token).ConfigureAwait(false);
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal static class MessagingBenchmarkExtensions
{
    public static async Task WaitOrThrowAsync(this Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != task)
            throw new TimeoutException($"{what} did not finish within 60 seconds.");
    }

}
