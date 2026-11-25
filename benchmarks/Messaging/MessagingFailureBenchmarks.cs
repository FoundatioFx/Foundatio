using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Queues;
using Foundatio.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Benchmarks.Messaging;

/// <summary>
/// The failure paths, per message, at handler concurrency 8 with 100-byte messages: a message whose handler fails once
/// and is retried at once, and a message that is dead-lettered. Each fails either by throwing, or without an exception
/// (<see cref="IMessageContext.Retry"/>, <see cref="IMessageContext.DeadLetter"/>, a failed <see cref="JobResult"/> for
/// the legacy queue job). Each invocation waits until every message is settled. <c>Success</c> is the same pipeline
/// when the handler succeeds.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[BenchmarkCategory("Messaging")]
public class MessagingFailureBenchmarks
{
    private const int Batch = 1000;
    private const int Concurrency = 8;
    private static readonly MessageSource Queue = MessageSource.Queue("bench.payload");

    private readonly Countdown _countdown = new();
    private BenchPayload[] _batch;
    private InMemoryMessageTransport _transport;
    private BenchHost _host;
    private IMessageClient _client;
    private InMemoryQueue<BenchPayload> _legacyQueue;
    private LegacyQueueWorkers _legacyWorkers;

    private enum Mode
    {
        Success,
        RetryOnceByThrowing,
        RetryOnceByContext,
        DeadLetterByThrowing,
        DeadLetterByContext
    }

    private async Task StartV2Async(Mode mode)
    {
        _batch = Enumerable.Repeat(BenchPayload.Create(100), Batch).ToArray();
        _transport = new InMemoryMessageTransport();
        var policy = mode is Mode.DeadLetterByThrowing ? RetryPolicy.None : new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.Zero) };
        _host = await BenchHost.StartAsync(s => s.AddFoundatioMessaging(m => m
            .UseTransport(_transport)
            .MapMessage<BenchPayload>("bench.payload")
            .AddConsumer<BenchPayload>((_, context, _) =>
            {
                _countdown.Signal();
                switch (mode)
                {
                    case Mode.RetryOnceByThrowing when context.Attempt == 1:
                    case Mode.DeadLetterByThrowing:
                        throw new InvalidOperationException("The handler failed.");
                    case Mode.RetryOnceByContext when context.Attempt == 1:
                        context.Retry(TimeSpan.Zero);
                        break;
                    case Mode.DeadLetterByContext:
                        context.DeadLetter("The handler gave up.");
                        break;
                }

                return Task.CompletedTask;
            }, new HandlerOptions { MaxConcurrency = Concurrency, RetryPolicy = policy })));
        _client = _host.Services.GetRequiredService<IMessageClient>();
    }

    private void StartLegacy(Mode mode)
    {
        _batch = Enumerable.Repeat(BenchPayload.Create(100), Batch).ToArray();
        _legacyQueue = new InMemoryQueue<BenchPayload>(o => o.RetryDelay(TimeSpan.Zero).Retries(mode == Mode.Success ? 2 : mode == Mode.RetryOnceByContext ? 1 : 0));
        _legacyWorkers = new LegacyQueueWorkers(_legacyQueue, Concurrency, entry =>
        {
            _countdown.Signal();
            return mode == Mode.Success || mode == Mode.RetryOnceByContext && entry.Attempts > 1 ? JobResult.Success : JobResult.FailedWithMessage("The handler failed.");
        });
    }

    [GlobalSetup(Target = nameof(V2Success))]
    public Task SetupV2SuccessAsync() => StartV2Async(Mode.Success);

    [GlobalSetup(Target = nameof(V2RetryOnceByThrowing))]
    public Task SetupV2RetryThrowingAsync() => StartV2Async(Mode.RetryOnceByThrowing);

    [GlobalSetup(Target = nameof(V2RetryOnceByContext))]
    public Task SetupV2RetryContextAsync() => StartV2Async(Mode.RetryOnceByContext);

    [GlobalSetup(Target = nameof(V2DeadLetterByThrowing))]
    public Task SetupV2DeadLetterThrowingAsync() => StartV2Async(Mode.DeadLetterByThrowing);

    [GlobalSetup(Target = nameof(V2DeadLetterByContext))]
    public Task SetupV2DeadLetterContextAsync() => StartV2Async(Mode.DeadLetterByContext);

    [GlobalSetup(Target = nameof(LegacyQueueSuccess))]
    public void SetupLegacySuccess() => StartLegacy(Mode.Success);

    [GlobalSetup(Target = nameof(LegacyQueueRetryOnce))]
    public void SetupLegacyRetry() => StartLegacy(Mode.RetryOnceByContext);

    [GlobalSetup(Target = nameof(LegacyQueueDeadLetter))]
    public void SetupLegacyDeadLetter() => StartLegacy(Mode.DeadLetterByContext);

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (_transport is not null)
            await _transport.DisposeAsync();
        if (_legacyWorkers is not null)
            await _legacyWorkers.DisposeAsync();
        _legacyQueue?.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Success")]
    public Task V2Success() => RunV2Async(Batch, deadLetters: false);

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Success")]
    public Task LegacyQueueSuccess() => RunLegacyAsync(Batch);

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    [BenchmarkCategory("RetryOnce")]
    public Task V2RetryOnceByThrowing() => RunV2Async(2 * Batch, deadLetters: false);

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("RetryOnce")]
    public Task V2RetryOnceByContext() => RunV2Async(2 * Batch, deadLetters: false);

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("RetryOnce")]
    public Task LegacyQueueRetryOnce() => RunLegacyAsync(2 * Batch);

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    [BenchmarkCategory("DeadLetter")]
    public Task V2DeadLetterByThrowing() => RunV2Async(Batch, deadLetters: true);

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("DeadLetter")]
    public Task V2DeadLetterByContext() => RunV2Async(Batch, deadLetters: true);

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("DeadLetter")]
    public Task LegacyQueueDeadLetter() => RunLegacyAsync(Batch);

    private async Task RunV2Async(int handled, bool deadLetters)
    {
        var done = _countdown.Reset(handled);
        await _client.SendBatchAsync(_batch);
        await done.WaitOrThrowAsync("The v2 failure benchmark");

        // Handlers return before their messages settle.
        var spin = new SpinWait();
        while (_transport.GetMessageCount(Queue) > 0)
            spin.SpinOnce();

        if (deadLetters)
            await _transport.DeleteAsync(MessageSource.DeadLetter(Queue));
    }

    private async Task RunLegacyAsync(int handled)
    {
        var done = _countdown.Reset(handled);
        foreach (var payload in _batch)
            await _legacyQueue.EnqueueAsync(payload);

        await done.WaitOrThrowAsync("The legacy failure benchmark");
        var spin = new SpinWait();
        while (await _legacyQueue.GetQueueStatsAsync() is { } stats && stats.Queued + stats.Working > 0)
            spin.SpinOnce();

        await _legacyQueue.DeleteQueueAsync();
    }
}
