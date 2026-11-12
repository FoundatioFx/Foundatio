using System;
using System.Collections.Generic;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>Settings for <see cref="KafkaMessageTransport"/>.</summary>
public sealed record KafkaMessageTransportOptions
{
    /// <summary>The brokers to connect to, for example "localhost:9092".</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// Prepended to every topic and consumer group name, so several environments or applications can share a cluster.
    /// Letters, digits, '.', '_' and '-' only.
    /// </summary>
    public string Prefix { get; init; } = "foundatio";

    /// <summary>
    /// Partitions for topics the transport creates, or <c>null</c> for the broker's <c>num.partitions</c>. Competing
    /// receivers of a queue or subscription share partitions, so use at least as many as you run instances.
    /// </summary>
    public int? Partitions { get; init; }

    /// <summary>Replication factor for topics the transport creates, or <c>null</c> for the broker's default.</summary>
    public short? ReplicationFactor { get; init; }

    /// <summary>Topic-level configs for topics the transport creates, for example <c>retention.ms</c>.</summary>
    public IReadOnlyDictionary<string, string>? TopicConfigs { get; init; }

    /// <summary>
    /// When true (the default), topics are created with the admin client before they are used. Turn it off when
    /// topics are provisioned ahead of time; every topic the transport uses must then exist.
    /// </summary>
    public bool CreateTopics { get; init; } = true;

    /// <summary>librdkafka properties applied to every producer, consumer and admin client (security, SASL, client id).</summary>
    public IReadOnlyDictionary<string, string>? ClientConfig { get; init; }

    /// <summary>Adjusts the producer configuration after the transport's defaults are applied.</summary>
    public Action<ProducerConfig>? ConfigureProducer { get; init; }

    /// <summary>
    /// Adjusts every consumer configuration after the transport's defaults are applied. The defaults use the classic
    /// group protocol with the cooperative-sticky assignor; set <see cref="ConsumerConfig.GroupProtocol"/> to
    /// <see cref="GroupProtocol.Consumer"/> to use the KIP-848 protocol on Kafka 4.0 or later.
    /// </summary>
    public Action<ConsumerConfig>? ConfigureConsumer { get; init; }

    /// <summary>
    /// When true (the default), delayed sends and delayed retries go through shared delay topics and a forwarder
    /// that runs in every process with a receiver. When false the transport declares no delay support, so Foundatio
    /// holds retries in-process and rejects delayed sends.
    /// </summary>
    public bool DelayedDelivery { get; init; } = true;

    /// <summary>
    /// How long a receiver waits, when a rebalance takes partitions away, for messages it already handed out from
    /// those partitions to be settled before it gives them up. Messages still unsettled after this are released.
    /// </summary>
    public TimeSpan RebalanceGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <see cref="KafkaMessageTransport.CreateReceiverAsync"/> waits for a queue or subscription receiver to
    /// join its consumer group and get its first assignment, so it can deliver as soon as it is returned. Joining a
    /// group that other receivers already use takes a rebalance. <see cref="TimeSpan.Zero"/> returns at once and
    /// joins in the background.
    /// </summary>
    public TimeSpan ReceiverJoinTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often completed offsets are committed. Uncommitted completions are delivered again after a crash.</summary>
    public TimeSpan CommitInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The largest record the producer sends (librdkafka <c>message.max.bytes</c>). The broker's and topics'
    /// <c>max.message.bytes</c> must allow it. The transport reserves part of it for its own headers.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 1_000_000;

    /// <summary>Used to compute delays. Kafka timestamps are wall-clock, so a fake clock only shifts due times.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Receives the transport's logs, including librdkafka's (its info and notice logs at debug level).</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>How long the delay forwarder waits before it tries again to forward a record it could not.</summary>
    internal TimeSpan DelayForwardRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times the delay forwarder tries a record that its target rejects before dropping it.</summary>
    internal int DelayForwardMaxAttempts { get; init; } = 12;
}
