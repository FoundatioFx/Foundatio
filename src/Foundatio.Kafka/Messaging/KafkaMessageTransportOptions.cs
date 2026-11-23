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
    /// receivers of a queue or subscription share partitions, so use at least as many as you run instances. Ordered
    /// groups are isolated from each other per partition too, so also use at least as many as groups you expect to be
    /// busy at once; the first grouped send to a topic with one partition logs a warning.
    /// </summary>
    public int? Partitions { get; init; }

    /// <summary>Replication factor for topics the transport creates, or <c>null</c> for the broker's default.</summary>
    public short? ReplicationFactor { get; init; }

    /// <summary>
    /// Topic-level configs for topics the transport creates, for example <c>retention.ms</c>. Delay and dead-letter
    /// topics take them too, except that they are never compacted and always keep their records for at least seven
    /// days (delay) or fourteen days (dead letters), whatever their size. Topics that already exist are not changed.
    /// </summary>
    public IReadOnlyDictionary<string, string>? TopicConfigs { get; init; }

    /// <summary>
    /// When true (the default), topics are created with the admin client before they are used. Turn it off when
    /// topics are provisioned ahead of time; every topic the transport uses must then exist.
    /// </summary>
    public bool CreateEntities { get; init; } = true;

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
    /// How long <see cref="IMessageTransport.CreateReceiverAsync"/> waits for a queue or subscription receiver to
    /// join its consumer group and get its first assignment, so it can deliver as soon as it is returned. Joining a
    /// group that other receivers already use takes a rebalance. <see cref="TimeSpan.Zero"/> returns at once and
    /// joins in the background.
    /// </summary>
    public TimeSpan ReceiverJoinTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often completed offsets are committed. Uncommitted completions are delivered again after a crash.</summary>
    public TimeSpan CommitInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The largest record the producer sends (librdkafka <c>message.max.bytes</c>). The broker's and topics'
    /// <c>max.message.bytes</c> must allow it. The transport reserves part of it for its own headers and declares the
    /// rest as <see cref="TransportCapabilities.MaxMessageSize"/>. At most 1,000,000,000, the librdkafka limit.
    /// </summary>
    public long MaxMessageSize { get; init; } = 1_000_000;

    /// <summary>Used to compute delays. Kafka timestamps are wall-clock, so a fake clock only shifts due times.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Receives the transport's logs, including librdkafka's (its info and notice logs at debug level).</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>How many records a receiver buffers ready to hand out before it stops reading, or <c>null</c> for one based on its prefetch count.</summary>
    internal int? ReceiverBufferLimit { get; init; }

    /// <summary>How many records a receiver holds in memory until their delivery time before it stops reading their partitions.</summary>
    internal int ReceiverTimedLimit { get; init; } = 10_000;

    /// <summary>How many records a receiver holds behind an earlier record of their group, per partition, before it stops reading that partition.</summary>
    internal int ReceiverHeldLimit { get; init; } = 1000;

    /// <summary>
    /// How many records of one group a receiver holds behind an earlier record of the group before it stops reading. If
    /// they do not drain to half within <see cref="ReceiverHeldStopTimeout"/>, it skips the group's later records
    /// instead, leaving them unfinished, and reads them again once the group drains.
    /// </summary>
    internal int ReceiverGroupHeldLimit { get; init; } = 100;

    /// <summary>How many records a receiver skips per partition before it stops reading that partition until half are read again.</summary>
    internal int ReceiverSkippedLimit { get; init; } = 10_000;

    /// <summary>
    /// How many records a receiver skips on all its partitions together, about 16 MB of their message ids and offsets,
    /// before no more groups start skipping and it stops reading the partitions with skipped records, then pauses them,
    /// until half are read again.
    /// </summary>
    internal int ReceiverSkippedTotalLimit { get; init; } = 100_000;

    /// <summary>
    /// How far behind a partition's last read offset its oldest skipped record may be before the receiver stops
    /// reading that partition until it is half as far. The partition's next owner reads again from its oldest
    /// unfinished record, usually the busy group's first unsettled one, which is older still.
    /// </summary>
    internal long ReceiverSkippedSpanLimit { get; init; } = 100_000;

    /// <summary>
    /// How long a receiver stops reading for a partition over its held limit before it pauses that partition instead,
    /// which drops what librdkafka fetched for it but lets its other partitions be read, and for a group over its held
    /// limit before it skips the group's later records instead.
    /// </summary>
    internal TimeSpan ReceiverHeldStopTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the delay forwarder waits before it tries again to forward a record it could not.</summary>
    internal TimeSpan DelayForwardRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times the delay forwarder tries a record that its target rejects before dropping it.</summary>
    internal int DelayForwardMaxAttempts { get; init; } = 12;

    /// <summary>How many forwarded records the delay forwarder has in flight before it stops reading the delay topics.</summary>
    internal int DelayForwardMaxInFlight { get; init; } = 10_000;

    /// <summary>How long a consumer kept open to read a group's committed offsets for stats stays open while unused.</summary>
    internal TimeSpan StatsReaderIdleTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How many consumers are kept open to read committed offsets for stats; past that, each read opens its own.</summary>
    internal int MaxStatsReaders { get; init; } = 32;
}
