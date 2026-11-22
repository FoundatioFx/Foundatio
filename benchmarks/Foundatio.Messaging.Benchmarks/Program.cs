using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging.Benchmarks;

/// <summary>
/// Measures v2 messaging, the legacy Foundatio API and the raw client SDK on the same broker. One process runs one
/// measurement and prints one JSON line; run it under <c>taskset</c> and interleave runs to compare.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = BenchOptions.Parse(args);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        await using var calls = BrokerCalls.Create(options);
        await using var target = TargetFactory.Create(options, calls);
        var runner = new Runner(options, target, calls);
        Result result;
        try
        {
            result = await runner.RunAsync(cancellation.Token);
        }
        finally
        {
            try
            {
                await target.StopAsync();
                await target.CleanupAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"cleanup failed: {ex.Message}");
            }
        }

        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
        Console.WriteLine(json);
        if (options.Output is not null)
            await File.AppendAllTextAsync(options.Output, json + Environment.NewLine);

        return 0;
    }
}

public sealed record Result
{
    public required string Run { get; init; }
    public string? Tag { get; init; }
    public required string Transport { get; init; }
    public required string Api { get; init; }
    public required string Pattern { get; init; }
    public required string Scenario { get; init; }
    public int Concurrency { get; init; }
    public int Payload { get; init; }
    public int Groups { get; init; }
    public double DelayMs { get; init; }
    public long Messages { get; init; }
    public long Duplicates { get; init; }
    public double Seconds { get; init; }
    public double MessagesPerSecond { get; init; }
    public double CpuMicrosecondsPerMessage { get; init; }
    public double CpuPercent { get; init; }
    public double AllocatedBytesPerMessage { get; init; }
    public long AllocatedBytes { get; init; }
    public int Gen0 { get; init; }
    public int Gen1 { get; init; }
    public int Gen2 { get; init; }
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public double? MaxMs { get; init; }
    public double? FirstMessageMs { get; init; }
    public double? StartMs { get; init; }
    public long BrokerCalls { get; init; }
    public double? BrokerCallsPerMessage { get; init; }
    public Dictionary<string, long> Calls { get; init; } = [];
    public string? Note { get; init; }
}

internal sealed class Snapshot
{
    public long Timestamp { get; } = Stopwatch.GetTimestamp();
    public TimeSpan Cpu { get; } = Process.GetCurrentProcess().TotalProcessorTime;
    public long Allocated { get; } = GC.GetTotalAllocatedBytes(precise: true);
    public int Gen0 { get; } = GC.CollectionCount(0);
    public int Gen1 { get; } = GC.CollectionCount(1);
    public int Gen2 { get; } = GC.CollectionCount(2);
}

public sealed class Runner(BenchOptions options, IBenchTarget target, BrokerCalls calls)
{
    private readonly string _data = new('x', options.Payload);
    private int _handled;
    private int _target = Int32.MaxValue;
    private long _duplicates;
    private byte[] _seen = [];
    private double[] _latencies = [];
    private long _firstHandled;
    private TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AllocationSampler? _sampler = options.AllocationTypes ? new AllocationSampler() : null;

    public async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"# {options.Describe()}");
        try
        {
            return await RunScenarioAsync(cancellationToken);
        }
        finally
        {
            _sampler?.Dispose();
        }
    }

    private async Task<Result> RunScenarioAsync(CancellationToken cancellationToken)
    {
        return options.Scenario switch
        {
            Scenario.Publish => await PublishAsync(batched: false, cancellationToken),
            Scenario.PublishBatch => await PublishAsync(batched: true, cancellationToken),
            Scenario.Consume => await ConsumeAsync(cancellationToken),
            Scenario.Latency => await LatencyAsync(cancellationToken),
            Scenario.Idle => await IdleAsync(cancellationToken),
            Scenario.Startup => await StartupAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }

    private BenchMessage NewMessage(int seq) => new()
    {
        Seq = seq,
        Data = _data,
        Group = options.Groups > 0 ? "g" + (seq % options.Groups) : null,
        Sent = Stopwatch.GetTimestamp()
    };

    private void Reset(int expected)
    {
        _handled = 0;
        _duplicates = 0;
        _target = expected;
        _seen = new byte[expected];
        _latencies = new double[expected];
        _firstHandled = 0;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async ValueTask HandleAsync(BenchMessage message)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _firstHandled, now, 0);
        if (options.WorkTime > TimeSpan.Zero)
            await Task.Delay(options.WorkTime);

        int seq = message.Seq;
        if ((uint)seq < (uint)_seen.Length)
        {
            if (Interlocked.Exchange(ref _seen[seq], 1) == 1)
            {
                Interlocked.Increment(ref _duplicates);
                return;
            }

            _latencies[seq] = Stopwatch.GetElapsedTime(message.Sent, now).TotalMilliseconds;
        }

        if (Interlocked.Increment(ref _handled) == _target)
            _done.TrySetResult();
    }

    private async Task SendAllAsync(int count, bool batched, int publishers, CancellationToken cancellationToken)
    {
        int next = -1;
        int batchSize = batched ? options.BatchSize : 1;
        await Task.WhenAll(Enumerable.Range(0, publishers).Select(_ => Task.Run(async () =>
        {
            var batch = new List<BenchMessage>(batchSize);
            while (true)
            {
                batch.Clear();
                while (batch.Count < batchSize)
                {
                    int seq = Interlocked.Increment(ref next);
                    if (seq >= count)
                        break;
                    batch.Add(NewMessage(seq));
                }

                if (batch.Count == 0)
                    return;
                if (batched)
                    await target.SendBatchAsync(batch, cancellationToken);
                else
                    await target.SendAsync(batch[0], cancellationToken);
            }
        }, cancellationToken)));
    }

    private async Task<Result> PublishAsync(bool batched, CancellationToken cancellationToken)
    {
        await target.InitAsync(cancellationToken);
        // warm up connections, JIT and topology
        await SendAllAsync(Math.Min(500, options.Count), batched, options.Publishers, cancellationToken);
        calls.Reset();
        var start = Begin();
        await SendAllAsync(options.Count, batched, options.Publishers, cancellationToken);
        return Measure(start, new Snapshot(), options.Count);
    }

    private async Task<Result> ConsumeAsync(CancellationToken cancellationToken)
    {
        await target.InitAsync(cancellationToken);
        Reset(options.Count);
        await SendAllAsync(options.Count, batched: true, Math.Min(options.Publishers, 8), cancellationToken);
        if (options.Delay <= TimeSpan.Zero)
            await Task.Delay(TransportSettleTime, cancellationToken);

        GC.Collect();
        calls.Reset();
        var start = Begin();
        await target.StartAsync(options.Concurrency, HandleAsync, cancellationToken);
        double startMs = Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds;
        await WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        var end = new Snapshot();
        double firstMs = _firstHandled == 0 ? 0 : Stopwatch.GetElapsedTime(start.Timestamp, _firstHandled).TotalMilliseconds;
        // throughput from the first handled message, so receiver start-up is reported separately
        var result = Measure(start, end, _handled);
        double seconds = _firstHandled == 0 ? result.Seconds : Stopwatch.GetElapsedTime(_firstHandled, end.Timestamp).TotalSeconds;
        return result with
        {
            Seconds = seconds,
            MessagesPerSecond = _handled / seconds,
            Duplicates = _duplicates,
            StartMs = startMs,
            FirstMessageMs = firstMs,
            Note = options.Delay > TimeSpan.Zero ? "throughput from first delayed delivery" : null
        };
    }

    private async Task<Result> LatencyAsync(CancellationToken cancellationToken)
    {
        await target.InitAsync(cancellationToken);
        int total = (int)(options.Rate * options.Duration.TotalSeconds);
        Reset(total);
        await target.StartAsync(options.Concurrency, HandleAsync, cancellationToken);
        await Task.Delay(1000, cancellationToken);

        calls.Reset();
        var start = Begin();
        using var outstanding = new SemaphoreSlim(256);
        var sends = new List<Task>(total);
        long begin = Stopwatch.GetTimestamp();
        for (int seq = 0; seq < total; seq++)
        {
            // pace: message seq is due at seq / rate seconds after begin
            double dueMs = seq * 1000.0 / options.Rate;
            double waitMs = dueMs - Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            if (waitMs > 1)
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken);

            await outstanding.WaitAsync(cancellationToken);
            var message = NewMessage(seq);
            sends.Add(SendReleasingAsync(message, outstanding, cancellationToken));
        }

        await Task.WhenAll(sends);
        await WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        var end = new Snapshot();
        var result = Measure(start, end, _handled);

        // leave out the first second, which includes connection warm-up
        int skip = Math.Min(options.Rate, total / 5);
        var latencies = _latencies.Skip(skip).Where((_, i) => _seen[i + skip] == 1).OrderBy(l => l).ToArray();
        return result with
        {
            Duplicates = _duplicates,
            P50Ms = Percentile(latencies, 0.50),
            P95Ms = Percentile(latencies, 0.95),
            P99Ms = Percentile(latencies, 0.99),
            MaxMs = latencies.Length > 0 ? latencies[^1] : null,
            Note = $"rate={options.Rate}/s for {options.Duration.TotalSeconds}s, handled {_handled}/{total}"
        };
    }

    private async Task SendReleasingAsync(BenchMessage message, SemaphoreSlim outstanding, CancellationToken cancellationToken)
    {
        try
        {
            await target.SendAsync(message, cancellationToken);
        }
        finally
        {
            outstanding.Release();
        }
    }

    private async Task<Result> IdleAsync(CancellationToken cancellationToken)
    {
        await target.InitAsync(cancellationToken);
        Reset(1);
        await target.StartAsync(options.Concurrency, HandleAsync, cancellationToken);
        await Task.Delay(5000, cancellationToken);
        GC.Collect();
        calls.Reset();
        var start = Begin();
        await Task.Delay(options.Duration, cancellationToken);
        var end = new Snapshot();
        var result = Measure(start, end, 0);
        double seconds = result.Seconds;
        return result with
        {
            Note = $"idle {seconds:F0}s: {result.BrokerCalls / seconds:F2} broker calls/s, {Stopwatch.GetElapsedTime(start.Timestamp).TotalSeconds:F0}s",
            BrokerCallsPerMessage = result.BrokerCalls / seconds
        };
    }

    private async Task<Result> StartupAsync(CancellationToken cancellationToken)
    {
        Reset(1);
        calls.Reset();
        var start = Begin();
        // a v2 receiver provisions its own queue or subscription, so its init (start and stop a receiver) is not needed
        if (options.Api != Api.V2)
            await target.InitAsync(cancellationToken);
        await target.StartAsync(options.Concurrency, HandleAsync, cancellationToken);
        double startMs = Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds;
        if (options.IdleBeforeFirst > TimeSpan.Zero)
            await Task.Delay(options.IdleBeforeFirst, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        await target.SendAsync(NewMessage(0), cancellationToken);
        await WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        var end = new Snapshot();
        var result = Measure(start, end, 1);
        return result with
        {
            StartMs = startMs,
            FirstMessageMs = Stopwatch.GetElapsedTime(started, _firstHandled).TotalMilliseconds,
            Note = "calls are everything from init to the first handled message"
        };
    }

    private TimeSpan TransportSettleTime => options.Transport == "servicebus" ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(500);

    private async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("BENCH_PROGRESS") is not null)
        {
            var started = Stopwatch.GetTimestamp();
            while (!_done.Task.IsCompleted && Stopwatch.GetElapsedTime(started) < timeout)
            {
                await Task.WhenAny(_done.Task, Task.Delay(1000, cancellationToken));
                Console.Error.WriteLine($"  {Stopwatch.GetElapsedTime(started).TotalSeconds:F0}s handled {Volatile.Read(ref _handled)}");
            }
        }

        var completed = await Task.WhenAny(_done.Task, Task.Delay(timeout, cancellationToken));
        if (completed != _done.Task)
            Console.Error.WriteLine($"timed out after {timeout}: handled {_handled}/{_target}");
    }

    private Snapshot Begin()
    {
        _sampler?.Start();
        return new Snapshot();
    }

    private Result Measure(Snapshot start, Snapshot end, long messages)
    {
        if (_sampler is not null)
            Console.Error.WriteLine("allocations by type:" + Environment.NewLine + _sampler.Stop());

        double seconds = Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalSeconds;
        double cpu = (end.Cpu - start.Cpu).TotalMilliseconds * 1000;
        long allocated = end.Allocated - start.Allocated;
        var brokerCalls = calls.Snapshot();
        long totalCalls = brokerCalls.Values.Sum();
        long perMessage = Math.Max(messages, 1);
        return new Result
        {
            Run = options.Describe(),
            Tag = options.Tag,
            Transport = options.Transport + (options.KafkaConsumerProtocol ? "-kip848" : "") + (options.Fifo ? "-fifo" : ""),
            Api = options.Api.ToString().ToLowerInvariant(),
            Pattern = options.Pattern.ToString().ToLowerInvariant(),
            Scenario = options.Scenario.ToString().ToLowerInvariant(),
            Concurrency = options.Concurrency,
            Payload = options.Payload,
            Groups = options.Groups,
            DelayMs = options.Delay.TotalMilliseconds,
            Messages = messages,
            Seconds = seconds,
            MessagesPerSecond = messages / seconds,
            CpuMicrosecondsPerMessage = cpu / perMessage,
            CpuPercent = cpu / 1000 / (seconds * 1000) * 100,
            AllocatedBytes = allocated,
            AllocatedBytesPerMessage = (double)allocated / perMessage,
            Gen0 = end.Gen0 - start.Gen0,
            Gen1 = end.Gen1 - start.Gen1,
            Gen2 = end.Gen2 - start.Gen2,
            BrokerCalls = totalCalls,
            BrokerCallsPerMessage = calls.IsSupported && messages > 0 ? (double)totalCalls / messages : null,
            Calls = brokerCalls
        };
    }

    private static double? Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? null : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
}
