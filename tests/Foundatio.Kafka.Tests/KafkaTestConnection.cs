using System;

namespace Foundatio.Kafka.Tests;

public static class KafkaTestConnection
{
    /// <summary>The brokers the tests use: the KAFKA_BOOTSTRAP_SERVERS environment variable, or the docker-compose broker.</summary>
    public static string BootstrapServers => Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") is { Length: > 0 } servers ? servers : "127.0.0.1:9092";

    /// <summary>Set KAFKA_GROUP_PROTOCOL=consumer to run the transport tests on the KIP-848 consumer group protocol.</summary>
    public static bool UseConsumerGroupProtocol => String.Equals(Environment.GetEnvironmentVariable("KAFKA_GROUP_PROTOCOL"), "consumer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The transport prefix of this test run. Runs on a shared broker get delay tiers and a delay consumer group of
    /// their own, so a run never forwards, or waits behind, another run's delayed messages. <see cref="KafkaTestCleanup"/>
    /// deletes what the run created.
    /// </summary>
    public static string Prefix { get; } = $"foundatio-tests-{Guid.NewGuid():N}"[..24];
}
