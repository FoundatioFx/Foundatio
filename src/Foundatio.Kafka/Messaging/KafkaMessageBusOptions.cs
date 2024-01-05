using System;
using System.Collections.Generic;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Foundatio.Messaging;

public class KafkaMessageBusOptions : SharedMessageBusOptions
{
    /// <summary>
    /// <inheritdoc cref="ClientConfig.BootstrapServers"/>
    /// </summary>
    public string BootstrapServers { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.GroupId"/>
    /// </summary>
    public string GroupId { get; set; }

    /// <summary>
    /// Resolve a message type from a custom source.
    /// </summary>
    public Func<ConsumeResult<string, byte[]>, string> ResolveMessageType { get; set; }

    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// <inheritdoc cref="Message{TKey,TValue}.Key"/>
    /// </summary>
    public string PublishKey { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.AutoCommitIntervalMs"/>
    /// </summary>
    public int? AutoCommitIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCertificateLocation"/>
    /// </summary>
    public string SslCertificateLocation { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslMechanism"/>
    /// </summary>
    public SaslMechanism? SaslMechanism { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslUsername"/>
    /// </summary>
    public string SaslUsername { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslPassword"/>
    /// </summary>
    public string SaslPassword { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCaLocation"/>
    /// </summary>
    public string SslCaLocation { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SecurityProtocol"/>
    /// </summary>
    public SecurityProtocol? SecurityProtocol { get; set; }

    /// <summary>
    /// <inheritdoc cref="TopicSpecification.NumPartitions"/>
    /// </summary>
    public int? TopicNumberOfPartitions { get; set; }

    /// <summary>
    /// <inheritdoc cref="TopicSpecification.ReplicationFactor"/>
    /// </summary>
    public short? TopicReplicationFactor { get; set; }

    /// <summary>
    /// <inheritdoc cref="TopicSpecification.Configs"/>
    /// </summary>
    public Dictionary<string, string> TopicConfigs { get; set; }

    /// <summary>
    /// <inheritdoc cref="TopicSpecification.ReplicasAssignments"/>
    /// </summary>
    public Dictionary<int, List<int>> TopicReplicasAssignments { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.Acks"/>
    /// </summary>
    public Acks? Acks { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ClientId"/>
    /// </summary>
    public string ClientId { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.MessageMaxBytes"/>
    /// </summary>
    public int? MessageMaxBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.MessageCopyMaxBytes"/>
    /// </summary>
    public int? MessageCopyMaxBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ReceiveMessageMaxBytes"/>
    /// </summary>
    public int? ReceiveMessageMaxBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.MaxInFlight"/>
    /// </summary>
    public int? MaxInFlight { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.TopicMetadataRefreshIntervalMs"/>
    /// </summary>
    public int? TopicMetadataRefreshIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.MetadataMaxAgeMs"/>
    /// </summary>
    public int? MetadataMaxAgeMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.TopicMetadataRefreshFastIntervalMs"/>
    /// </summary>
    public int? TopicMetadataRefreshFastIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.TopicMetadataRefreshSparse"/>
    /// </summary>
    public bool? TopicMetadataRefreshSparse { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.TopicMetadataPropagationMaxMs"/>
    /// </summary>
    public int? TopicMetadataPropagationMaxMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.TopicBlacklist"/>
    /// </summary>
    public string TopicBlacklist { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.Debug"/>
    /// </summary>
    public string Debug { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SocketTimeoutMs"/>
    /// </summary>
    public int? SocketTimeoutMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SocketSendBufferBytes"/>
    /// </summary>
    public int? SocketSendBufferBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SocketReceiveBufferBytes"/>
    /// </summary>
    public int? SocketReceiveBufferBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SocketKeepaliveEnable"/>
    /// </summary>
    public bool? SocketKeepaliveEnable { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SocketNagleDisable"/>
    /// </summary>
    public bool? SocketNagleDisable { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SocketMaxFails"/>
    /// </summary>
    public int? SocketMaxFails { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.BrokerAddressTtl"/>
    /// </summary>
    public int? BrokerAddressTtl { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.BrokerAddressFamily"/>
    /// </summary>
    public BrokerAddressFamily? BrokerAddressFamily { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ConnectionsMaxIdleMs"/>
    /// </summary>
    public int? ConnectionsMaxIdleMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ReconnectBackoffMs"/>
    /// </summary>
    public int? ReconnectBackoffMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ReconnectBackoffMaxMs"/>
    /// </summary>
    public int? ReconnectBackoffMaxMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.StatisticsIntervalMs"/>
    /// </summary>
    public int? StatisticsIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.LogQueue"/>
    /// </summary>
    public bool? LogQueue { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.LogThreadName"/>
    /// </summary>
    public bool? LogThreadName { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.EnableRandomSeed"/>
    /// </summary>
    public bool? EnableRandomSeed { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.LogConnectionClose"/>
    /// </summary>
    public bool? LogConnectionClose { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.InternalTerminationSignal"/>
    /// </summary>
    public int? InternalTerminationSignal { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ApiVersionRequest"/>
    /// </summary>
    public bool? ApiVersionRequest { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ApiVersionRequestTimeoutMs"/>
    /// </summary>
    public int? ApiVersionRequestTimeoutMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ApiVersionFallbackMs"/>
    /// </summary>
    public int? ApiVersionFallbackMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.BrokerVersionFallback"/>
    /// </summary>
    public string BrokerVersionFallback { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCipherSuites"/>
    /// </summary>
    public string SslCipherSuites { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCurvesList"/>
    /// </summary>
    public string SslCurvesList { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslSigalgsList"/>
    /// </summary>
    public string SslSigalgsList { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslKeyLocation"/>
    /// </summary>
    public string SslKeyLocation { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslKeyPassword"/>
    /// </summary>
    public string SslKeyPassword { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslKeyPem"/>
    /// </summary>
    public string SslKeyPem { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCertificatePem"/>
    /// </summary>
    public string SslCertificatePem { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCaPem"/>
    /// </summary>
    public string SslCaPem { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCaCertificateStores"/>
    /// </summary>
    public string SslCaCertificateStores { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslCrlLocation"/>
    /// </summary>
    public string SslCrlLocation { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslKeystoreLocation"/>
    /// </summary>
    public string SslKeystoreLocation { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslKeystorePassword"/>
    /// </summary>
    public string SslKeystorePassword { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslEngineLocation"/>
    /// </summary>
    public string SslEngineLocation { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslEngineId"/>
    /// </summary>
    public string SslEngineId { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.EnableSslCertificateVerification"/>
    /// </summary>
    public bool? EnableSslCertificateVerification { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SslEndpointIdentificationAlgorithm"/>
    /// </summary>
    public SslEndpointIdentificationAlgorithm? SslEndpointIdentificationAlgorithm { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslKerberosServiceName"/>
    /// </summary>
    public string SaslKerberosServiceName { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslKerberosPrincipal"/>
    /// </summary>
    public string SaslKerberosPrincipal { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslKerberosKinitCmd"/>
    /// </summary>
    public string SaslKerberosKinitCmd { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslKerberosKeytab"/>
    /// </summary>
    public string SaslKerberosKeytab { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslKerberosMinTimeBeforeRelogin"/>
    /// </summary>
    public int? SaslKerberosMinTimeBeforeRelogin { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.SaslOauthbearerConfig"/>
    /// </summary>
    public string SaslOauthbearerConfig { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.EnableSaslOauthbearerUnsecureJwt"/>
    /// </summary>
    public bool? EnableSaslOauthbearerUnsecureJwt { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.PluginLibraryPaths"/>
    /// </summary>
    public string PluginLibraryPaths { get; set; }

    /// <summary>
    /// <inheritdoc cref="ClientConfig.ClientRack"/>
    /// </summary>
    public string ClientRack { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.EnableBackgroundPoll"/>
    /// </summary>
    public bool? EnableBackgroundPoll { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.EnableDeliveryReports"/>
    /// </summary>
    public bool? EnableDeliveryReports { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.DeliveryReportFields"/>
    /// </summary>
    public string DeliveryReportFields { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.RequestTimeoutMs"/>
    /// </summary>
    public int? RequestTimeoutMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.MessageTimeoutMs"/>
    /// </summary>
    public int? MessageTimeoutMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.Partitioner"/>
    /// </summary>
    public Partitioner? Partitioner { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.CompressionLevel"/>
    /// </summary>
    public int? CompressionLevel { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.TransactionalId"/>
    /// </summary>
    public string TransactionalId { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.TransactionTimeoutMs"/>
    /// </summary>
    public int? TransactionTimeoutMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.EnableIdempotence"/>
    /// </summary>
    public bool? EnableIdempotence { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.EnableGaplessGuarantee"/>
    /// </summary>
    public bool? EnableGaplessGuarantee { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.QueueBufferingMaxMessages"/>
    /// </summary>
    public int? QueueBufferingMaxMessages { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.QueueBufferingMaxKbytes"/>
    /// </summary>
    public int? QueueBufferingMaxKbytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.LingerMs"/>
    /// </summary>
    public double? LingerMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.MessageSendMaxRetries"/>
    /// </summary>
    public int? MessageSendMaxRetries { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.RetryBackoffMs"/>
    /// </summary>
    public int? RetryBackoffMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.QueueBufferingBackpressureThreshold"/>
    /// </summary>
    public int? QueueBufferingBackpressureThreshold { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.CompressionType"/>
    /// </summary>
    public CompressionType? CompressionType { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.BatchNumMessages"/>
    /// </summary>
    public int? BatchNumMessages { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.BatchSize"/>
    /// </summary>
    public int? BatchSize { get; set; }

    /// <summary>
    /// <inheritdoc cref="ProducerConfig.StickyPartitioningLingerMs"/>
    /// </summary>
    public int? StickyPartitioningLingerMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.ConsumeResultFields"/>
    /// </summary>
    public string ConsumeResultFields { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.AutoOffsetReset"/>
    /// </summary>
    public AutoOffsetReset? AutoOffsetReset { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.GroupInstanceId"/>
    /// </summary>
    public string GroupInstanceId { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.PartitionAssignmentStrategy"/>
    /// </summary>
    public PartitionAssignmentStrategy? PartitionAssignmentStrategy { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.SessionTimeoutMs"/>
    /// </summary>
    public int? SessionTimeoutMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.HeartbeatIntervalMs"/>
    /// </summary>
    public int? HeartbeatIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.GroupProtocolType"/>
    /// </summary>
    public string GroupProtocolType { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.CoordinatorQueryIntervalMs"/>
    /// </summary>
    public int? CoordinatorQueryIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.MaxPollIntervalMs"/>
    /// </summary>
    public int? MaxPollIntervalMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.EnableAutoCommit"/>
    /// </summary>
    public bool? EnableAutoCommit { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.EnableAutoOffsetStore"/>
    /// </summary>
    public bool? EnableAutoOffsetStore { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.QueuedMinMessages"/>
    /// </summary>
    public int? QueuedMinMessages { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.QueuedMaxMessagesKbytes"/>
    /// </summary>
    public int? QueuedMaxMessagesKbytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.FetchWaitMaxMs"/>
    /// </summary>
    public int? FetchWaitMaxMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.MaxPartitionFetchBytes"/>
    /// </summary>
    public int? MaxPartitionFetchBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.FetchMaxBytes"/>
    /// </summary>
    public int? FetchMaxBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.FetchMinBytes"/>
    /// </summary>
    public int? FetchMinBytes { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.FetchErrorBackoffMs"/>
    /// </summary>
    public int? FetchErrorBackoffMs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.IsolationLevel"/>
    /// </summary>
    public IsolationLevel? IsolationLevel { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.EnablePartitionEof"/>
    /// </summary>
    public bool? EnablePartitionEof { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.EnablePartitionEof"/>
    /// </summary>
    public bool? CheckCrcs { get; set; }

    /// <summary>
    /// <inheritdoc cref="ConsumerConfig.AllowAutoCreateTopics"/>
    /// </summary>
    public bool? AllowAutoCreateTopics { get; set; }
}

public class KafkaMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<KafkaMessageBusOptions, KafkaMessageBusOptionsBuilder>
{
    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.BootstrapServers"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder BootstrapServers(string bootstrapServers)
    {
        Target.BootstrapServers = bootstrapServers ?? throw new ArgumentNullException(nameof(bootstrapServers));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ResolveMessageType"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ResolveMessageType(Func<ConsumeResult<string, byte[]>, string> resolveMessageType)
    {
        Target.ResolveMessageType = resolveMessageType ?? throw new ArgumentNullException(nameof(resolveMessageType));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.GroupId"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder GroupId(string groupId)
    {
        Target.GroupId = groupId ?? throw new ArgumentNullException(nameof(groupId));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.AutoCommitIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder AutoCommitIntervalMs(int autoCommitIntervalMs)
    {
        Target.AutoCommitIntervalMs = autoCommitIntervalMs;
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.PublishKey"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder PublishKey(string publishKey)
    {
        Target.PublishKey = publishKey ?? throw new ArgumentNullException(nameof(publishKey));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCertificateLocation"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCertificateLocation(string sslCertificateLocation)
    {
        Target.SslCertificateLocation = sslCertificateLocation ?? throw new ArgumentNullException(nameof(sslCertificateLocation));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslMechanism"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslMechanism(SaslMechanism? saslMechanism)
    {
        Target.SaslMechanism = saslMechanism ?? throw new ArgumentNullException(nameof(saslMechanism));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslUsername"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslUsername(string saslUsername)
    {
        Target.SaslUsername = saslUsername ?? throw new ArgumentNullException(nameof(saslUsername));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslPassword"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslPassword(string saslPassword)
    {
        Target.SaslPassword = saslPassword ?? throw new ArgumentNullException(nameof(saslPassword));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCaLocation"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCaLocation(string sslCaLocation)
    {
        Target.SslCaLocation = sslCaLocation ?? throw new ArgumentNullException(nameof(sslCaLocation));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SecurityProtocol"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SecurityProtocol(SecurityProtocol? securityProtocol)
    {
        Target.SecurityProtocol = securityProtocol ?? throw new ArgumentNullException(nameof(securityProtocol));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.AutoCommitIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder AutoCommitIntervalMs(int? autoCommitIntervalMs)
    {
        Target.AutoCommitIntervalMs = autoCommitIntervalMs ?? throw new ArgumentNullException(nameof(autoCommitIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicNumberOfPartitions"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicNumberOfPartitions(int? numberOfPartitions)
    {
        Target.TopicNumberOfPartitions = numberOfPartitions ?? throw new ArgumentNullException(nameof(numberOfPartitions));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicReplicationFactor"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicReplicationFactor(short? replicationFactor)
    {
        Target.TopicReplicationFactor = replicationFactor ?? throw new ArgumentNullException(nameof(replicationFactor));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.Acks"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder Acks(Acks? acks)
    {
        Target.Acks = acks ?? throw new ArgumentNullException(nameof(acks));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ClientId"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ClientId(string clientId)
    {
        Target.ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MessageMaxBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MessageMaxBytes(int? messageMaxBytes)
    {
        Target.MessageMaxBytes = messageMaxBytes ?? throw new ArgumentNullException(nameof(messageMaxBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MessageCopyMaxBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MessageCopyMaxBytes(int? messageCopyMaxBytes)
    {
        Target.MessageCopyMaxBytes = messageCopyMaxBytes ?? throw new ArgumentNullException(nameof(messageCopyMaxBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ReceiveMessageMaxBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ReceiveMessageMaxBytes(int? receiveMessageMaxBytes)
    {
        Target.ReceiveMessageMaxBytes = receiveMessageMaxBytes ?? throw new ArgumentNullException(nameof(receiveMessageMaxBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MaxInFlight"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MaxInFlight(int? maxInFlight)
    {
        Target.MaxInFlight = maxInFlight ?? throw new ArgumentNullException(nameof(maxInFlight));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicConfigs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicConfigs(Dictionary<string, string> topicConfigs)
    {
        Target.TopicConfigs = topicConfigs ?? throw new ArgumentNullException(nameof(topicConfigs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicReplicasAssignments"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicReplicasAssignments(Dictionary<int, List<int>> topicReplicasAssignments)
    {
        Target.TopicReplicasAssignments = topicReplicasAssignments ?? throw new ArgumentNullException(nameof(topicReplicasAssignments));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicMetadataRefreshIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicMetadataRefreshIntervalMs(int? topicMetadataRefreshIntervalMs)
    {
        Target.TopicMetadataRefreshIntervalMs = topicMetadataRefreshIntervalMs ?? throw new ArgumentNullException(nameof(topicMetadataRefreshIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MetadataMaxAgeMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MetadataMaxAgeMs(int? metadataMaxAgeMs)
    {
        Target.MetadataMaxAgeMs = metadataMaxAgeMs ?? throw new ArgumentNullException(nameof(metadataMaxAgeMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicMetadataRefreshFastIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicMetadataRefreshFastIntervalMs(int? topicMetadataRefreshFastIntervalMs)
    {
        Target.TopicMetadataRefreshFastIntervalMs = topicMetadataRefreshFastIntervalMs ?? throw new ArgumentNullException(nameof(topicMetadataRefreshFastIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicMetadataRefreshSparse"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicMetadataRefreshSparse(bool? topicMetadataRefreshSparse)
    {
        Target.TopicMetadataRefreshSparse = topicMetadataRefreshSparse ?? throw new ArgumentNullException(nameof(topicMetadataRefreshSparse));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicMetadataPropagationMaxMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicMetadataPropagationMaxMs(int? topicMetadataPropagationMaxMs)
    {
        Target.TopicMetadataPropagationMaxMs = topicMetadataPropagationMaxMs ?? throw new ArgumentNullException(nameof(topicMetadataPropagationMaxMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TopicBlacklist"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TopicBlacklist(string topicBlacklist)
    {
        Target.TopicBlacklist = topicBlacklist ?? throw new ArgumentNullException(nameof(topicBlacklist));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.Debug"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder Debug(string debug)
    {
        Target.Debug = debug ?? throw new ArgumentNullException(nameof(debug));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SocketTimeoutMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SocketTimeoutMs(int? socketTimeoutMs)
    {
        Target.SocketTimeoutMs = socketTimeoutMs ?? throw new ArgumentNullException(nameof(socketTimeoutMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SocketSendBufferBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SocketSendBufferBytes(int? socketSendBufferBytes)
    {
        Target.SocketSendBufferBytes = socketSendBufferBytes ?? throw new ArgumentNullException(nameof(socketSendBufferBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SocketReceiveBufferBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SocketReceiveBufferBytes(int? socketReceiveBufferBytes)
    {
        Target.SocketReceiveBufferBytes = socketReceiveBufferBytes ?? throw new ArgumentNullException(nameof(socketReceiveBufferBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SocketKeepaliveEnable"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SocketKeepaliveEnable(bool? socketKeepaliveEnable)
    {
        Target.SocketKeepaliveEnable = socketKeepaliveEnable ?? throw new ArgumentNullException(nameof(socketKeepaliveEnable));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SocketNagleDisable"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SocketNagleDisable(bool? socketNagleDisable)
    {
        Target.SocketNagleDisable = socketNagleDisable ?? throw new ArgumentNullException(nameof(socketNagleDisable));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SocketMaxFails"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SocketMaxFails(int? socketMaxFails)
    {
        Target.SocketMaxFails = socketMaxFails ?? throw new ArgumentNullException(nameof(socketMaxFails));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.BootstrapServers"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder BrokerAddressTtl(int? brokerAddressTtl)
    {
        Target.BrokerAddressTtl = brokerAddressTtl ?? throw new ArgumentNullException(nameof(brokerAddressTtl));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.BrokerAddressFamily"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder BrokerAddressFamily(BrokerAddressFamily? brokerAddressFamily)
    {
        Target.BrokerAddressFamily = brokerAddressFamily ?? throw new ArgumentNullException(nameof(brokerAddressFamily));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ConnectionsMaxIdleMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ConnectionsMaxIdleMs(int? connectionsMaxIdleMs)
    {
        Target.ConnectionsMaxIdleMs = connectionsMaxIdleMs ?? throw new ArgumentNullException(nameof(connectionsMaxIdleMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ReconnectBackoffMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ReconnectBackoffMs(int? reconnectBackoffMs)
    {
        Target.ReconnectBackoffMs = reconnectBackoffMs ?? throw new ArgumentNullException(nameof(reconnectBackoffMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ReconnectBackoffMaxMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ReconnectBackoffMaxMs(int? reconnectBackoffMaxMs)
    {
        Target.ReconnectBackoffMaxMs = reconnectBackoffMaxMs ?? throw new ArgumentNullException(nameof(reconnectBackoffMaxMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.StatisticsIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder StatisticsIntervalMs(int? statisticsIntervalMs)
    {
        Target.StatisticsIntervalMs = statisticsIntervalMs ?? throw new ArgumentNullException(nameof(statisticsIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.LogQueue"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder LogQueue(bool? logQueue)
    {
        Target.LogQueue = logQueue ?? throw new ArgumentNullException(nameof(logQueue));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.LogThreadName"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder LogThreadName(bool? logThreadName)
    {
        Target.LogThreadName = logThreadName ?? throw new ArgumentNullException(nameof(logThreadName));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableRandomSeed"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableRandomSeed(bool? enableRandomSeed)
    {
        Target.EnableRandomSeed = enableRandomSeed ?? throw new ArgumentNullException(nameof(enableRandomSeed));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.LogConnectionClose"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder LogConnectionClose(bool? logConnectionClose)
    {
        Target.LogConnectionClose = logConnectionClose ?? throw new ArgumentNullException(nameof(logConnectionClose));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.InternalTerminationSignal"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder InternalTerminationSignal(int? internalTerminationSignal)
    {
        Target.InternalTerminationSignal = internalTerminationSignal ?? throw new ArgumentNullException(nameof(internalTerminationSignal));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ApiVersionRequest"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ApiVersionRequest(bool? apiVersionRequest)
    {
        Target.ApiVersionRequest = apiVersionRequest ?? throw new ArgumentNullException(nameof(apiVersionRequest));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ApiVersionRequestTimeoutMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ApiVersionRequestTimeoutMs(int? apiVersionRequestTimeoutMs)
    {
        Target.ApiVersionRequestTimeoutMs = apiVersionRequestTimeoutMs ?? throw new ArgumentNullException(nameof(apiVersionRequestTimeoutMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ApiVersionFallbackMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ApiVersionFallbackMs(int? apiVersionFallbackMs)
    {
        Target.ApiVersionFallbackMs = apiVersionFallbackMs ?? throw new ArgumentNullException(nameof(apiVersionFallbackMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.BrokerVersionFallback"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder BrokerVersionFallback(string brokerVersionFallback)
    {
        Target.BrokerVersionFallback = brokerVersionFallback ?? throw new ArgumentNullException(nameof(brokerVersionFallback));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCipherSuites"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCipherSuites(string sslCipherSuites)
    {
        Target.SslCipherSuites = sslCipherSuites ?? throw new ArgumentNullException(nameof(sslCipherSuites));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCurvesList"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCurvesList(string sslCurvesList)
    {
        Target.SslCurvesList = sslCurvesList ?? throw new ArgumentNullException(nameof(sslCurvesList));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslSigalgsList"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslSigalgsList(string sslSigalgsList)
    {
        Target.SslSigalgsList = sslSigalgsList ?? throw new ArgumentNullException(nameof(sslSigalgsList));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslKeyLocation"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslKeyLocation(string sslKeyLocation)
    {
        Target.SslKeyLocation = sslKeyLocation ?? throw new ArgumentNullException(nameof(sslKeyLocation));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslKeyPassword"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslKeyPassword(string sslKeyPassword)
    {
        Target.SslKeyPassword = sslKeyPassword ?? throw new ArgumentNullException(nameof(sslKeyPassword));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslKeyPem"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslKeyPem(string sslKeyPem)
    {
        Target.SslKeyPem = sslKeyPem ?? throw new ArgumentNullException(nameof(sslKeyPem));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCertificatePem"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCertificatePem(string sslCertificatePem)
    {
        Target.SslCertificatePem = sslCertificatePem ?? throw new ArgumentNullException(nameof(sslCertificatePem));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCaPem"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCaPem(string sslCaPem)
    {
        Target.SslCaPem = sslCaPem ?? throw new ArgumentNullException(nameof(sslCaPem));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCaCertificateStores"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCaCertificateStores(string sslCaCertificateStores)
    {
        Target.SslCaCertificateStores = sslCaCertificateStores ?? throw new ArgumentNullException(nameof(sslCaCertificateStores));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslCrlLocation"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslCrlLocation(string sslCrlLocation)
    {
        Target.SslCrlLocation = sslCrlLocation ?? throw new ArgumentNullException(nameof(sslCrlLocation));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslKeystoreLocation"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslKeystoreLocation(string sslKeystoreLocation)
    {
        Target.SslKeystoreLocation = sslKeystoreLocation ?? throw new ArgumentNullException(nameof(sslKeystoreLocation));
        return this;
    }
    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslKeystorePassword"/>
    /// </summary>

    public KafkaMessageBusOptionsBuilder SslKeystorePassword(string sslKeystorePassword)
    {
        Target.SslKeystorePassword = sslKeystorePassword ?? throw new ArgumentNullException(nameof(sslKeystorePassword));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslEngineLocation"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslEngineLocation(string sslEngineLocation)
    {
        Target.SslEngineLocation = sslEngineLocation ?? throw new ArgumentNullException(nameof(sslEngineLocation));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslEngineId"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslEngineId(string sslEngineId)
    {
        Target.SslEngineId = sslEngineId ?? throw new ArgumentNullException(nameof(sslEngineId));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableSslCertificateVerification"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableSslCertificateVerification(bool? enableSslCertificateVerification)
    {
        Target.EnableSslCertificateVerification = enableSslCertificateVerification ?? throw new ArgumentNullException(nameof(enableSslCertificateVerification));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SslEndpointIdentificationAlgorithm"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SslEndpointIdentificationAlgorithm(SslEndpointIdentificationAlgorithm? sslEndpointIdentificationAlgorithm)
    {
        Target.SslEndpointIdentificationAlgorithm = sslEndpointIdentificationAlgorithm ?? throw new ArgumentNullException(nameof(sslEndpointIdentificationAlgorithm));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslKerberosServiceName"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslKerberosServiceName(string saslKerberosServiceName)
    {
        Target.SaslKerberosServiceName = saslKerberosServiceName ?? throw new ArgumentNullException(nameof(saslKerberosServiceName));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslKerberosPrincipal"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslKerberosPrincipal(string saslKerberosPrincipal)
    {
        Target.SaslKerberosPrincipal = saslKerberosPrincipal ?? throw new ArgumentNullException(nameof(saslKerberosPrincipal));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslKerberosKinitCmd"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslKerberosKinitCmd(string saslKerberosKinitCmd)
    {
        Target.SaslKerberosKinitCmd = saslKerberosKinitCmd ?? throw new ArgumentNullException(nameof(saslKerberosKinitCmd));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslKerberosKeytab"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslKerberosKeytab(string saslKerberosKeytab)
    {
        Target.SaslKerberosKeytab = saslKerberosKeytab ?? throw new ArgumentNullException(nameof(saslKerberosKeytab));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslKerberosMinTimeBeforeRelogin"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslKerberosMinTimeBeforeRelogin(int? saslKerberosMinTimeBeforeRelogin)
    {
        Target.SaslKerberosMinTimeBeforeRelogin = saslKerberosMinTimeBeforeRelogin ?? throw new ArgumentNullException(nameof(saslKerberosMinTimeBeforeRelogin));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SaslOauthbearerConfig"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SaslOauthbearerConfig(string saslOauthbearerConfig)
    {
        Target.SaslOauthbearerConfig = saslOauthbearerConfig ?? throw new ArgumentNullException(nameof(saslOauthbearerConfig));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableSaslOauthbearerUnsecureJwt"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableSaslOauthbearerUnsecureJwt(bool? enableSaslOauthbearerUnsecureJwt)
    {
        Target.EnableSaslOauthbearerUnsecureJwt = enableSaslOauthbearerUnsecureJwt ?? throw new ArgumentNullException(nameof(enableSaslOauthbearerUnsecureJwt));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.PluginLibraryPaths"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder PluginLibraryPaths(string pluginLibraryPaths)
    {
        Target.PluginLibraryPaths = pluginLibraryPaths ?? throw new ArgumentNullException(nameof(pluginLibraryPaths));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ClientRack"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ClientRack(string clientRack)
    {
        Target.ClientRack = clientRack ?? throw new ArgumentNullException(nameof(clientRack));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableBackgroundPoll"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableBackgroundPoll(bool? enableBackgroundPoll)
    {
        Target.EnableBackgroundPoll = enableBackgroundPoll ?? throw new ArgumentNullException(nameof(enableBackgroundPoll));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableDeliveryReports"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableDeliveryReports(bool? enableDeliveryReports)
    {
        Target.EnableDeliveryReports = enableDeliveryReports ?? throw new ArgumentNullException(nameof(enableDeliveryReports));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.DeliveryReportFields"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder DeliveryReportFields(string deliveryReportFields)
    {
        Target.DeliveryReportFields = deliveryReportFields ?? throw new ArgumentNullException(nameof(deliveryReportFields));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.RequestTimeoutMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder RequestTimeoutMs(int? requestTimeoutMs)
    {
        Target.RequestTimeoutMs = requestTimeoutMs ?? throw new ArgumentNullException(nameof(requestTimeoutMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MessageTimeoutMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MessageTimeoutMs(int? messageTimeoutMs)
    {
        Target.MessageTimeoutMs = messageTimeoutMs ?? throw new ArgumentNullException(nameof(messageTimeoutMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.Partitioner"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder Partitioner(Partitioner? partitioner)
    {
        Target.Partitioner = partitioner ?? throw new ArgumentNullException(nameof(partitioner));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.CompressionLevel"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder CompressionLevel(int? compressionLevel)
    {
        Target.CompressionLevel = compressionLevel ?? throw new ArgumentNullException(nameof(compressionLevel));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TransactionalId"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TransactionalId(string transactionalId)
    {
        Target.TransactionalId = transactionalId ?? throw new ArgumentNullException(nameof(transactionalId));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.TransactionTimeoutMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder TransactionTimeoutMs(int? transactionTimeoutMs)
    {
        Target.TransactionTimeoutMs = transactionTimeoutMs ?? throw new ArgumentNullException(nameof(transactionTimeoutMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableIdempotence"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableIdempotence(bool? enableIdempotence)
    {
        Target.EnableIdempotence = enableIdempotence ?? throw new ArgumentNullException(nameof(enableIdempotence));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableGaplessGuarantee"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableGaplessGuarantee(bool? enableGaplessGuarantee)
    {
        Target.EnableGaplessGuarantee = enableGaplessGuarantee ?? throw new ArgumentNullException(nameof(enableGaplessGuarantee));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.QueueBufferingMaxMessages"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder QueueBufferingMaxMessages(int? queueBufferingMaxMessages)
    {
        Target.QueueBufferingMaxMessages = queueBufferingMaxMessages ?? throw new ArgumentNullException(nameof(queueBufferingMaxMessages));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.QueueBufferingMaxKbytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder QueueBufferingMaxKbytes(int? queueBufferingMaxKbytes)
    {
        Target.QueueBufferingMaxKbytes = queueBufferingMaxKbytes ?? throw new ArgumentNullException(nameof(queueBufferingMaxKbytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.LingerMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder LingerMs(double? lingerMs)
    {
        Target.LingerMs = lingerMs ?? throw new ArgumentNullException(nameof(lingerMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MessageSendMaxRetries"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MessageSendMaxRetries(int? messageSendMaxRetries)
    {
        Target.MessageSendMaxRetries = messageSendMaxRetries ?? throw new ArgumentNullException(nameof(messageSendMaxRetries));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.RetryBackoffMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder RetryBackoffMs(int? retryBackoffMs)
    {
        Target.RetryBackoffMs = retryBackoffMs ?? throw new ArgumentNullException(nameof(retryBackoffMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.QueueBufferingBackpressureThreshold"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder QueueBufferingBackpressureThreshold(int? queueBufferingBackpressureThreshold)
    {
        Target.QueueBufferingBackpressureThreshold = queueBufferingBackpressureThreshold ?? throw new ArgumentNullException(nameof(queueBufferingBackpressureThreshold));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.CompressionType"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder CompressionType(CompressionType? compressionType)
    {
        Target.CompressionType = compressionType ?? throw new ArgumentNullException(nameof(compressionType));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.BatchNumMessages"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder BatchNumMessages(int? batchNumMessages)
    {
        Target.BatchNumMessages = batchNumMessages ?? throw new ArgumentNullException(nameof(batchNumMessages));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.BatchSize"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder BatchSize(int? batchSize)
    {
        Target.BatchSize = batchSize ?? throw new ArgumentNullException(nameof(batchSize));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.StickyPartitioningLingerMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder StickyPartitioningLingerMs(int? stickyPartitioningLingerMs)
    {
        Target.StickyPartitioningLingerMs = stickyPartitioningLingerMs ?? throw new ArgumentNullException(nameof(stickyPartitioningLingerMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.ConsumeResultFields"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder ConsumeResultFields(string consumeResultFields)
    {
        Target.ConsumeResultFields = consumeResultFields ?? throw new ArgumentNullException(nameof(consumeResultFields));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.AutoOffsetReset"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder AutoOffsetReset(AutoOffsetReset? autoOffsetReset)
    {
        Target.AutoOffsetReset = autoOffsetReset ?? throw new ArgumentNullException(nameof(autoOffsetReset));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.GroupInstanceId"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder GroupInstanceId(string groupInstanceId)
    {
        Target.GroupInstanceId = groupInstanceId ?? throw new ArgumentNullException(nameof(groupInstanceId));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.PartitionAssignmentStrategy"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder PartitionAssignmentStrategy(PartitionAssignmentStrategy? partitionAssignmentStrategy)
    {
        Target.PartitionAssignmentStrategy = partitionAssignmentStrategy ?? throw new ArgumentNullException(nameof(partitionAssignmentStrategy));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.SessionTimeoutMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder SessionTimeoutMs(int? sessionTimeoutMs)
    {
        Target.SessionTimeoutMs = sessionTimeoutMs ?? throw new ArgumentNullException(nameof(sessionTimeoutMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.HeartbeatIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder HeartbeatIntervalMs(int? heartbeatIntervalMs)
    {
        Target.HeartbeatIntervalMs = heartbeatIntervalMs ?? throw new ArgumentNullException(nameof(heartbeatIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.GroupProtocolType"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder GroupProtocolType(string groupProtocolType)
    {
        Target.GroupProtocolType = groupProtocolType ?? throw new ArgumentNullException(nameof(groupProtocolType));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.CoordinatorQueryIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder CoordinatorQueryIntervalMs(int? coordinatorQueryIntervalMs)
    {
        Target.CoordinatorQueryIntervalMs = coordinatorQueryIntervalMs ?? throw new ArgumentNullException(nameof(coordinatorQueryIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MaxPollIntervalMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MaxPollIntervalMs(int? maxPollIntervalMs)
    {
        Target.MaxPollIntervalMs = maxPollIntervalMs ?? throw new ArgumentNullException(nameof(maxPollIntervalMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableAutoCommit"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableAutoCommit(bool? enableAutoCommit)
    {
        Target.EnableAutoCommit = enableAutoCommit ?? throw new ArgumentNullException(nameof(enableAutoCommit));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnableAutoOffsetStore"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnableAutoOffsetStore(bool? enableAutoOffsetStore)
    {
        Target.EnableAutoOffsetStore = enableAutoOffsetStore ?? throw new ArgumentNullException(nameof(enableAutoOffsetStore));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.QueuedMinMessages"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder QueuedMinMessages(int? queuedMinMessages)
    {
        Target.QueuedMinMessages = queuedMinMessages ?? throw new ArgumentNullException(nameof(queuedMinMessages));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.QueuedMaxMessagesKbytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder QueuedMaxMessagesKbytes(int? queuedMaxMessagesKbytes)
    {
        Target.QueuedMaxMessagesKbytes = queuedMaxMessagesKbytes ?? throw new ArgumentNullException(nameof(queuedMaxMessagesKbytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.FetchErrorBackoffMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder FetchErrorBackoffMs(int? fetchErrorBackoffMs)
    {
        Target.FetchErrorBackoffMs = fetchErrorBackoffMs ?? throw new ArgumentNullException(nameof(fetchErrorBackoffMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.FetchWaitMaxMs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder FetchWaitMaxMs(int? fetchWaitMaxMs)
    {
        Target.FetchWaitMaxMs = fetchWaitMaxMs ?? throw new ArgumentNullException(nameof(fetchWaitMaxMs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.MaxPartitionFetchBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder MaxPartitionFetchBytes(int? maxPartitionFetchBytes)
    {
        Target.MaxPartitionFetchBytes = maxPartitionFetchBytes ?? throw new ArgumentNullException(nameof(maxPartitionFetchBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.FetchMaxBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder FetchMaxBytes(int? fetchMaxBytes)
    {
        Target.FetchMaxBytes = fetchMaxBytes ?? throw new ArgumentNullException(nameof(fetchMaxBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.FetchMinBytes"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder FetchMinBytes(int? fetchMinBytes)
    {
        Target.FetchMinBytes = fetchMinBytes ?? throw new ArgumentNullException(nameof(fetchMinBytes));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.IsolationLevel"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder IsolationLevel(IsolationLevel? isolationLevel)
    {
        Target.IsolationLevel = isolationLevel ?? throw new ArgumentNullException(nameof(isolationLevel));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.EnablePartitionEof"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder EnablePartitionEof(bool? enablePartitionEof)
    {
        Target.EnablePartitionEof = enablePartitionEof ?? throw new ArgumentNullException(nameof(enablePartitionEof));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.CheckCrcs"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder CheckCrcs(bool? checkCrcs)
    {
        Target.CheckCrcs = checkCrcs ?? throw new ArgumentNullException(nameof(checkCrcs));
        return this;
    }

    /// <summary>
    /// <inheritdoc cref="KafkaMessageBusOptions.AllowAutoCreateTopics"/>
    /// </summary>
    public KafkaMessageBusOptionsBuilder AllowAutoCreateTopics(bool? allowAutoCreateTopics)
    {
        Target.AllowAutoCreateTopics = allowAutoCreateTopics ?? throw new ArgumentNullException(nameof(allowAutoCreateTopics));
        return this;
    }
}
