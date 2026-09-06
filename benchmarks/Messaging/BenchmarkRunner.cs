using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Foundatio.Messaging.Benchmarks;

public static class BenchmarkRunner
{
    public static async Task<int> RunAsync(BenchmarkOptions options, CancellationToken token)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        string prefix = "fperf-" + Guid.NewGuid().ToString("N")[..12];
        var environment = new Dictionary<string, string>
        {
            ["Runtime"] = RuntimeInformation.FrameworkDescription,
            ["OS"] = RuntimeInformation.OSDescription,
            ["Architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["LogicalProcessors"] = Environment.ProcessorCount.ToString(),
            ["ServerGC"] = GCSettings.IsServerGC.ToString(),
            ["Foundatio"] = VersionOf(typeof(MessageBus).Assembly),
            ["MassTransit"] = VersionOf(typeof(MassTransit.IBus).Assembly),
            ["SqsSdk"] = VersionOf(typeof(Amazon.SQS.AmazonSQSClient).Assembly),
            ["SnsSdk"] = VersionOf(typeof(Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient).Assembly),
            ["Broker"] = options.Transport == "sqs" ? (AwsResources.ServiceUrl is null ? "AWS (live)" : "SQS/SNS custom endpoint") : options.Transport
        };
        IMessagingDriver driver = options.Engine switch
        {
            "masstransit" => new MassTransitDriver(options, prefix),
            "loopback" => new LoopbackDriver(options),
            _ => new FoundatioDriver(options, prefix)
        };
        Console.WriteLine($"RUN {prefix} {options.Engine}/{options.Transport}/{options.Scenario}");
        DeliveryTracker? tracker = null;
        var trackers = new List<DeliveryTracker>();
        PhaseResult? measurement = null;
        string? error = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(options.DrainSeconds));
            await driver.StartAsync((group, message) => Volatile.Read(ref tracker)?.Record(group, message), startup.Token);
            if (options.WarmupSeconds > 0)
            {
                var warmup = await PhaseAsync(options.WarmupSeconds, true);
                if (!Valid(warmup)) throw new InvalidOperationException("Warmup failed: " + (warmup.Error ?? $"missing={warmup.Missing}, invalid={warmup.Invalid}, duplicates={warmup.Duplicates}, firstInvalid={tracker?.FirstInvalid}"));
            }
            measurement = await PhaseAsync(options.DurationSeconds, false);
            if (!Valid(measurement)) error = measurement.Error ?? "Delivery validation failed or the tracking limit was reached.";
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            try { await driver.DisposeAsync(); }
            catch (Exception ex) { error = (error is null ? "" : error + Environment.NewLine) + "Cleanup: " + ex; }
            foreach (var item in trackers) item.Dispose();
        }
        if (measurement is not null && tracker is not null)
        {
            measurement = measurement with { Duplicates = tracker.Duplicates, Invalid = tracker.InvalidDeliveries, FirstInvalid = tracker.FirstInvalid, Missing = tracker.ExpectedInputs * options.DeliveryCopies - tracker.UniqueDeliveries };
            if (!Valid(measurement) && error is null) error = "Delivery validation failed during shutdown.";
        }
        var result = new BenchmarkResult { Options = options, StartedUtc = startedUtc, ResourcePrefix = prefix, Environment = environment, Success = error is null, Error = error, Measurement = measurement };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
        await File.WriteAllTextAsync(options.Output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        Console.WriteLine($"{(result.Success ? "PASS" : "FAIL")} {options.Engine}/{options.Transport}/{options.Scenario} inputs/s={measurement?.InputsPerSecond:F0} deliveries/s={measurement?.DeliveriesPerSecond:F0} p99={measurement?.DeliveryLatency.P99Milliseconds:F2}ms output={options.Output}");
        if (error is not null) Console.Error.WriteLine(error);
        return result.Success ? 0 : 1;

        async Task<PhaseResult> PhaseAsync(int seconds, bool warmup)
        {
            Console.WriteLine($"PHASE {(warmup ? "warmup" : "measurement")} {seconds}s");
            string runId = Guid.NewGuid().ToString("N");
            string payload = new('x', options.PayloadBytes);
            int capacity = warmup ? Math.Min(options.MaxMessages, 1_000_000) : options.MaxMessages;
            var phaseTracker = new DeliveryTracker(runId, capacity, options.DeliveryCopies, options.MaxOutstanding, payload);
            trackers.Add(phaseTracker);
            Volatile.Write(ref tracker, phaseTracker);
            var sendLatency = new LatencyHistogram();
            using var process = Process.GetCurrentProcess();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var publishing = CancellationTokenSource.CreateLinkedTokenSource(token);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocatedStart = GC.GetTotalAllocatedBytes(true);
            TimeSpan cpuStart = process.TotalProcessorTime, pausesStart = GC.GetTotalPauseDuration();
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            long start = Stopwatch.GetTimestamp();
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds + options.DrainSeconds));
            publishing.CancelAfter(TimeSpan.FromSeconds(seconds));
            int next = 0, hitLimit = 0;
            double publishSeconds = 0;
            string? phaseError = null;
            var samples = new List<ProgressSample>();
            using var sampling = new CancellationTokenSource();
            var sampleTask = SampleAsync();
            try
            {
                await Task.WhenAll(Enumerable.Range(0, options.ProducerConcurrency).Select(_ => Task.Run(ProduceAsync, CancellationToken.None)));
                publishSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                while (phaseTracker.OutstandingInputs > 0)
                {
                    driver.ThrowIfFaulted();
                    await Task.Delay(5, deadline.Token);
                }
                driver.ThrowIfFaulted();
            }
            catch (Exception ex) { phaseError = ex.ToString(); }
            double totalSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedStart;
            TimeSpan cpu = process.TotalProcessorTime - cpuStart, pauses = GC.GetTotalPauseDuration() - pausesStart;
            for (int i = 0; i < 3; i++) collections[i] = GC.CollectionCount(i) - collections[i];
            await sampling.CancelAsync(); await sampleTask;
            process.Refresh();
            var gcMemory = GC.GetGCMemoryInfo();
            long peak = Math.Max(process.WorkingSet64, samples.Count == 0 ? 0 : samples.Max(s => s.WorkingSetBytes));
            return new PhaseResult
            {
                Error = phaseError,
                FirstInvalid = phaseTracker.FirstInvalid,
                Inputs = phaseTracker.ExpectedInputs,
                Deliveries = phaseTracker.UniqueDeliveries,
                Duplicates = phaseTracker.Duplicates,
                Invalid = phaseTracker.InvalidDeliveries,
                Missing = phaseTracker.ExpectedInputs * options.DeliveryCopies - phaseTracker.UniqueDeliveries,
                HitTrackingLimit = !warmup && hitLimit != 0,
                PublishSeconds = publishSeconds,
                TotalSeconds = totalSeconds,
                AllocatedBytes = allocated,
                CpuMilliseconds = cpu.TotalMilliseconds,
                GcPauseMilliseconds = pauses.TotalMilliseconds,
                PeakWorkingSetBytes = peak,
                GcHeapSizeBytes = gcMemory.HeapSizeBytes,
                GcCommittedBytes = gcMemory.TotalCommittedBytes,
                GcFragmentedBytes = gcMemory.FragmentedBytes,
                Collections = collections,
                DeliveryLatency = phaseTracker.Latency.Snapshot(),
                SendCallLatency = sendLatency.Snapshot(),
                Samples = samples
            };

            async Task ProduceAsync()
            {
                while (!publishing.IsCancellationRequested)
                {
                    try { await phaseTracker.ReserveAsync(options.BatchSize, publishing.Token); }
                    catch (OperationCanceledException) when (publishing.IsCancellationRequested) { return; }
                    int sequence = Interlocked.Add(ref next, options.BatchSize) - options.BatchSize;
                    int count = Math.Min(options.BatchSize, capacity - sequence);
                    if (count <= 0) { phaseTracker.ReleaseUnused(options.BatchSize); Interlocked.Exchange(ref hitLimit, 1); return; }
                    if (count < options.BatchSize) phaseTracker.ReleaseUnused(options.BatchSize - count);
                    long timestamp = options.RatePerSecond == 0 ? Stopwatch.GetTimestamp()
                        : start + (long)(sequence * (double)Stopwatch.Frequency / options.RatePerSecond);
                    if (options.RatePerSecond > 0)
                    {
                        try { await RateSchedule.WaitUntilAsync(timestamp, publishing.Token); }
                        catch (OperationCanceledException) when (publishing.IsCancellationRequested) { phaseTracker.ReleaseUnused(count); return; }
                    }
                    if (publishing.IsCancellationRequested) { phaseTracker.ReleaseUnused(count); return; }
                    var batch = new LoadMessage[count];
                    for (int i = 0; i < count; i++)
                    {
                        phaseTracker.Expect(sequence + i);
                        batch[i] = new LoadMessage(runId, sequence + i, timestamp, payload);
                    }
                    driver.ThrowIfFaulted();
                    long sendStart = Stopwatch.GetTimestamp();
                    await driver.SendAsync(batch, deadline.Token);
                    sendLatency.RecordMicroseconds((long)Stopwatch.GetElapsedTime(sendStart).TotalMicroseconds);
                }
            }

            async Task SampleAsync()
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), sampling.Token);
                        process.Refresh();
                        var sampleMemory = GC.GetGCMemoryInfo();
                        samples.Add(new(Stopwatch.GetElapsedTime(start).TotalSeconds, phaseTracker.ExpectedInputs, phaseTracker.UniqueDeliveries,
                            phaseTracker.OutstandingInputs, process.WorkingSet64, GC.GetTotalAllocatedBytes(false) - allocatedStart,
                            sampleMemory.HeapSizeBytes, sampleMemory.TotalCommittedBytes, sampleMemory.FragmentedBytes));
                    }
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            }
        }
    }

    private static bool Valid(PhaseResult result) => result.Error is null && result.Inputs > 0 && result.Missing == 0 && result.Invalid == 0 && result.Duplicates == 0 && !result.HitTrackingLimit;
    private static string VersionOf(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version!.ToString();
}

internal sealed class LoopbackDriver(BenchmarkOptions options) : IMessagingDriver
{
    private Action<int, LoadMessage> _received = null!;
    public Task StartAsync(Action<int, LoadMessage> received, CancellationToken token) { _received = received; return Task.CompletedTask; }
    public Task SendAsync(LoadMessage[] messages, CancellationToken token)
    {
        foreach (var message in messages) for (int group = 0; group < options.DeliveryCopies; group++) _received(group, message);
        return Task.CompletedTask;
    }
    public void ThrowIfFaulted() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
