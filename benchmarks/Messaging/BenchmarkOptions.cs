using System.Globalization;

namespace Foundatio.Messaging.Benchmarks;

public sealed record BenchmarkOptions
{
    public string Engine { get; init; } = "foundatio";
    public string Transport { get; init; } = "memory";
    public string Scenario { get; init; } = "queue";
    public int DurationSeconds { get; init; } = 15;
    public int WarmupSeconds { get; init; } = 3;
    public int DrainSeconds { get; init; } = 120;
    public int ProducerConcurrency { get; init; } = 32;
    public int ConsumerConcurrency { get; init; } = 32;
    public int Prefetch { get; init; } = 32;
    public int Subscribers { get; init; } = 4;
    public int PayloadBytes { get; init; } = 1024;
    public int BatchSize { get; init; } = 1;
    public int MaxOutstanding { get; init; } = 4096;
    public int MaxMessages { get; init; } = 10_000_000;
    public int RatePerSecond { get; init; }
    public string Output { get; init; } = "result.json";
    public int DeliveryCopies => Scenario == "queue" ? 1 : Subscribers;

    public void Validate()
    {
        if (Engine is not ("foundatio" or "masstransit" or "loopback")) throw new ArgumentException("Engine must be foundatio, masstransit or loopback.");
        if (Transport is not ("memory" or "redis" or "sqs")) throw new ArgumentException("Transport must be memory, redis or sqs.");
        if (Engine == "masstransit" && Transport == "redis") throw new ArgumentException("MassTransit has no Redis Streams transport.");
        if (Engine == "loopback" && Transport != "memory") throw new ArgumentException("Loopback measures only harness overhead.");
        if (Scenario is not ("queue" or "pubsub")) throw new ArgumentException("Scenario must be queue or pubsub.");
        if (DurationSeconds is < 1 or > 3600 || WarmupSeconds is < 0 or > 60 || DrainSeconds is < 1 or > 600) throw new ArgumentException("Invalid measurement/warmup/drain duration.");
        if (ProducerConcurrency is < 1 or > 1024 || ConsumerConcurrency is < 1 or > 1024 || Prefetch is < 1 or > 4096) throw new ArgumentException("Invalid concurrency or prefetch.");
        if (Subscribers is < 1 or > 32 || PayloadBytes is < 0 or > 131072 || BatchSize is < 1 or > 64) throw new ArgumentException("Invalid fanout, payload size or batch size.");
        if (MaxOutstanding < ProducerConcurrency * BatchSize || MaxOutstanding > 1_000_000) throw new ArgumentException("Outstanding window must hold one entire batch for every producer, and cannot exceed one million inputs.");
        if (MaxMessages < MaxOutstanding || MaxMessages > 100_000_000 || RatePerSecond < 0) throw new ArgumentException("Invalid tracking capacity or offered rate.");
    }

    public static BenchmarkOptions Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options use --name value pairs; use --help for examples.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Duplicate option {args[i]}.");
        string Text(string name, string fallback) => values.Remove("--" + name, out var value) ? value : fallback;
        int Number(string name, int fallback) => Int32.Parse(Text(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var options = new BenchmarkOptions
        {
            Engine = Text("engine", "foundatio"),
            Transport = Text("transport", "memory"),
            Scenario = Text("scenario", "queue"),
            DurationSeconds = Number("seconds", 15),
            WarmupSeconds = Number("warmup", 3),
            DrainSeconds = Number("drain", 120),
            ProducerConcurrency = Number("producers", 32),
            ConsumerConcurrency = Number("consumers", 32),
            Prefetch = Number("prefetch", 32),
            Subscribers = Number("subscribers", 4),
            PayloadBytes = Number("payload", 1024),
            BatchSize = Number("batch", 1),
            MaxOutstanding = Number("outstanding", 4096),
            MaxMessages = Number("max-messages", 10_000_000),
            RatePerSecond = Number("rate", 0),
            Output = Text("output", "result.json")
        };
        if (values.Count > 0) throw new ArgumentException($"Unknown option {values.Keys.First()}.");
        options.Validate();
        return options;
    }
}
