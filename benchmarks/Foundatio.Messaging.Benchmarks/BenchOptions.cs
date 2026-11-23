using System;
using System.Collections.Generic;
using System.Globalization;

namespace Foundatio.Messaging.Benchmarks;

public enum Api { V2, Legacy, Raw }

public enum Pattern { Queue, Topic }

public enum Scenario { Publish, PublishBatch, Consume, Latency, Idle, Startup }

/// <summary>Command-line options: <c>--transport redis --api v2 --scenario consume --concurrency 16 --payload 100</c>.</summary>
public sealed record BenchOptions
{
    public required string Transport { get; init; }
    public Api Api { get; init; } = Api.V2;
    public Pattern Pattern { get; init; } = Pattern.Queue;
    public Scenario Scenario { get; init; } = Scenario.Consume;
    public int Count { get; init; } = 20_000;
    public int Concurrency { get; init; } = 16;
    public int Publishers { get; init; } = 16;
    public int BatchSize { get; init; } = 100;
    public int Payload { get; init; } = 100;
    public int Rate { get; init; } = 1_000;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(20);
    public int Groups { get; init; }
    public TimeSpan Delay { get; init; }
    public TimeSpan WorkTime { get; init; }
    public bool Fifo { get; init; }
    public bool KafkaConsumerProtocol { get; init; }
    public int? KafkaLingerMs { get; init; }
    public string? Output { get; init; }
    public string? Tag { get; init; }
    public bool AllocationTypes { get; init; }
    public string Name { get; init; } = "b" + Guid.NewGuid().ToString("N")[..8];
    public string? RabbitConnection { get; init; } = Environment.GetEnvironmentVariable("RABBITMQ_CONNECTION") ?? "amqp://guest:guest@127.0.0.1:5672";
    public string? RabbitManagement { get; init; } = Environment.GetEnvironmentVariable("RABBITMQ_MANAGEMENT");
    public string Redis { get; init; } = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:7600,abortConnect=false,allowAdmin=true";
    public string Kafka { get; init; } = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "127.0.0.1:9092";
    public string Sqs { get; init; } = Environment.GetEnvironmentVariable("SQS_CONNECTION") ?? "serviceurl=http://127.0.0.1:4566;AccessKey=xxx;SecretKey=xxx";
    public string AzureStorage { get; init; } = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION") ?? "UseDevelopmentStorage=true";
    public TimeSpan IdleBeforeFirst { get; init; }
    public string ServiceBus { get; init; } = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION") ?? "Endpoint=sb://127.0.0.1:5672/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

    public static BenchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument {args[i]}");

            string key = args[i][2..];
            values[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        string Get(string key, string fallback) => values.TryGetValue(key, out string? value) ? value : fallback;
        int GetInt(string key, int fallback) => values.TryGetValue(key, out string? value) ? Int32.Parse(value, CultureInfo.InvariantCulture) : fallback;
        TimeSpan GetTime(string key, TimeSpan fallback) => values.TryGetValue(key, out string? value) ? TimeSpan.FromMilliseconds(Double.Parse(value, CultureInfo.InvariantCulture)) : fallback;

        var defaults = new BenchOptions { Transport = "redis" };
        return new BenchOptions
        {
            Transport = Get("transport", "redis").ToLowerInvariant(),
            Api = Enum.Parse<Api>(Get("api", "v2"), ignoreCase: true),
            Pattern = Enum.Parse<Pattern>(Get("pattern", "queue"), ignoreCase: true),
            Scenario = Enum.Parse<Scenario>(Get("scenario", "consume").Replace("-", ""), ignoreCase: true),
            Count = GetInt("count", defaults.Count),
            Concurrency = GetInt("concurrency", defaults.Concurrency),
            Publishers = GetInt("publishers", defaults.Publishers),
            BatchSize = GetInt("batch", defaults.BatchSize),
            Payload = GetInt("payload", defaults.Payload),
            Rate = GetInt("rate", defaults.Rate),
            Duration = TimeSpan.FromSeconds(GetInt("duration", (int)defaults.Duration.TotalSeconds)),
            Groups = GetInt("groups", 0),
            Delay = GetTime("delay-ms", TimeSpan.Zero),
            WorkTime = GetTime("work-ms", TimeSpan.Zero),
            IdleBeforeFirst = GetTime("idle-ms", TimeSpan.Zero),
            Fifo = values.ContainsKey("fifo"),
            KafkaConsumerProtocol = values.ContainsKey("kip848"),
            KafkaLingerMs = values.TryGetValue("kafka-linger-ms", out string? linger) ? Int32.Parse(linger, CultureInfo.InvariantCulture) : null,
            Output = values.TryGetValue("out", out string? output) ? output : null,
            AllocationTypes = values.ContainsKey("alloc-types"),
            Tag = values.TryGetValue("tag", out string? tag) && tag != "true" ? tag : null,
            Name = Get("name", defaults.Name)
        };
    }

    public string Describe() =>
        $"{Transport}{(KafkaConsumerProtocol ? "/kip848" : "")}{(KafkaLingerMs is { } linger ? $"/linger{linger}" : "")}{(Fifo ? "/fifo" : "")} {Api} {Pattern} {Scenario} c={Concurrency} p={Payload}B" +
        (Groups > 0 ? $" groups={Groups}" : "") + (Delay > TimeSpan.Zero ? $" delay={Delay.TotalMilliseconds}ms" : "") +
        (WorkTime > TimeSpan.Zero ? $" work={WorkTime.TotalMilliseconds}ms" : "");
}
