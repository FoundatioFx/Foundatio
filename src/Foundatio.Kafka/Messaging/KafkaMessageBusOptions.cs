using System;
using System.Collections.Generic;
using Confluent.Kafka;

namespace Foundatio.Messaging;

public class KafkaMessageBusOptions : SharedMessageBusOptions {

    public string BootstrapServers { get; set; }

    public string GroupId { get; set; }

    public string ContentType { get; set; } = "application/json";

    public string PublishKey { get; set; }

    public int? AutoCommitIntervalMs { get; set; }

    public string SslCertificateLocation { get; set; }

    public SaslMechanism? SaslMechanism { get; set; }

    public string SaslUsername { get; set; }

    public string SaslPassword { get; set; }

    public string SslCaLocation { get; set; }

    public SecurityProtocol? SecurityProtocol { get; set; }

    public int? TopicNumberOfPartitions { get; set; }

    public short? TopicReplicationFactor { get; set; }

    public Dictionary<string, string> TopicConfigs { get; set; }

    public Dictionary<int, List<int>> TopicReplicasAssignments { get; set; }

    public Acks? Acks { get; set; }

    public string ClientId { get; set; }

    public int? MessageMaxBytes { get; set; }

    public int? MessageCopyMaxBytes { get; set; }

    public int? ReceiveMessageMaxBytes { get; set; }

    public int? MaxInFlight { get; set; }

    public int? TopicMetadataRefreshIntervalMs { get; set; }

    public int? MetadataMaxAgeMs { get; set; }

    public int? TopicMetadataRefreshFastIntervalMs { get; set; }

    public bool? TopicMetadataRefreshSparse { get; set; }

    public int? TopicMetadataPropagationMaxMs { get; set; }

    public string TopicBlacklist { get; set; }

    public string Debug { get; set; }

    public int? SocketTimeoutMs { get; set; }

    public int? SocketSendBufferBytes { get; set; }

    public int? SocketReceiveBufferBytes { get; set; }

    public bool? SocketKeepaliveEnable { get; set; }

    public bool? SocketNagleDisable { get; set; }

    public int? SocketMaxFails { get; set; }

    public int? BrokerAddressTtl { get; set; }

    public BrokerAddressFamily? BrokerAddressFamily { get; set; }

    public int? ConnectionsMaxIdleMs { get; set; }

    public int? ReconnectBackoffMs { get; set; }

    public int? ReconnectBackoffMaxMs { get; set; }

    public int? StatisticsIntervalMs { get; set; }

    public bool? LogQueue { get; set; }

    public bool? LogThreadName { get; set; }

    public bool? EnableRandomSeed { get; set; }

    public bool? LogConnectionClose { get; set; }

    public int? InternalTerminationSignal { get; set; }

    public bool? ApiVersionRequest { get; set; }

    public int? ApiVersionRequestTimeoutMs { get; set; }

    public int? ApiVersionFallbackMs { get; set; }

    public string BrokerVersionFallback { get; set; }

    public string SslCipherSuites { get; set; }

    public string SslCurvesList { get; set; }

    public string SslSigalgsList { get; set; }

    public string SslKeyLocation { get; set; }

    public string SslKeyPassword { get; set; }

    public string SslKeyPem { get; set; }

    public string SslCertificatePem { get; set; }

    public string SslCaPem { get; set; }

    public string SslCaCertificateStores { get; set; }

    public string SslCrlLocation { get; set; }

    public string SslKeystoreLocation { get; set; }

    public string SslKeystorePassword { get; set; }

    public string SslEngineLocation { get; set; }

    public string SslEngineId { get; set; }

    public bool? EnableSslCertificateVerification { get; set; }

    public SslEndpointIdentificationAlgorithm? SslEndpointIdentificationAlgorithm { get; set; }

    public string SaslKerberosServiceName { get; set; }

    public string SaslKerberosPrincipal { get; set; }

    public string SaslKerberosKinitCmd { get; set; }

    public string SaslKerberosKeytab { get; set; }

    public int? SaslKerberosMinTimeBeforeRelogin { get; set; }

    public string SaslOauthbearerConfig { get; set; }

    public bool? EnableSaslOauthbearerUnsecureJwt { get; set; }

    public string PluginLibraryPaths { get; set; }

    public string ClientRack { get; set; }

    public bool? EnableBackgroundPoll { get; set; }

    public bool? EnableDeliveryReports { get; set; }

    public string DeliveryReportFields { get; set; }

    public int? RequestTimeoutMs { get; set; }

    public int? MessageTimeoutMs { get; set; }

    public Partitioner? Partitioner { get; set; }

    public int? CompressionLevel { get; set; }

    public string TransactionalId { get; set; }

    public int? TransactionTimeoutMs { get; set; }

    public bool? EnableIdempotence { get; set; }

    public bool? EnableGaplessGuarantee { get; set; }

    public int? QueueBufferingMaxMessages { get; set; }

    public int? QueueBufferingMaxKbytes { get; set; }

    public double? LingerMs { get; set; }

    public int? MessageSendMaxRetries { get; set; }

    public int? RetryBackoffMs { get; set; }

    public int? QueueBufferingBackpressureThreshold { get; set; }

    public CompressionType? CompressionType { get; set; }

    public int? BatchNumMessages { get; set; }

    public int? BatchSize { get; set; }

    public int? StickyPartitioningLingerMs { get; set; }

    public string ConsumeResultFields { get; set; }

    public AutoOffsetReset? AutoOffsetReset { get; set; }

    public string GroupInstanceId { get; set; }

    public PartitionAssignmentStrategy? PartitionAssignmentStrategy { get; set; }

    public int? SessionTimeoutMs { get; set; }

    public int? HeartbeatIntervalMs { get; set; }

    public string GroupProtocolType { get; set; }

    public int? CoordinatorQueryIntervalMs { get; set; }

    public int? MaxPollIntervalMs { get; set; }

    public bool? EnableAutoCommit { get; set; }

    public bool? EnableAutoOffsetStore { get; set; }

    public int? QueuedMinMessages { get; set; }

    public int? QueuedMaxMessagesKbytes { get; set; }

    public int? FetchWaitMaxMs { get; set; }

    public int? MaxPartitionFetchBytes { get; set; }

    public int? FetchMaxBytes { get; set; }

    public int? FetchMinBytes { get; set; }

    public int? FetchErrorBackoffMs { get; set; }

    public IsolationLevel? IsolationLevel { get; set; }

    public bool? EnablePartitionEof { get; set; }

    public bool? CheckCrcs { get; set; }

    public bool? AllowAutoCreateTopics { get; set; } = false;
}
public class KafkaMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<KafkaMessageBusOptions, KafkaMessageBusOptionsBuilder> {
    public KafkaMessageBusOptionsBuilder BootStrapServers(string bootstrapServers) {
        Target.BootstrapServers = bootstrapServers ?? throw new ArgumentNullException(nameof(bootstrapServers));
        return this;
    }

    public KafkaMessageBusOptionsBuilder GroupId(string groupId) {
        Target.GroupId = groupId ?? throw new ArgumentNullException(nameof(groupId));
        return this;
    }

    public KafkaMessageBusOptionsBuilder AutoCommitIntervalMs(int autoCommitIntervalMs) {
        Target.AutoCommitIntervalMs = autoCommitIntervalMs;
        return this;
    }

    public KafkaMessageBusOptionsBuilder PublishKey(string publishKey) {
        Target.PublishKey = publishKey ?? throw new ArgumentNullException(nameof(publishKey));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCertificateLocation(string sslCertificateLocation) {
        Target.SslCertificateLocation = sslCertificateLocation ?? throw new ArgumentNullException(nameof(sslCertificateLocation));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslMechanism(SaslMechanism? saslMechanism) {
        Target.SaslMechanism = saslMechanism ?? throw new ArgumentNullException(nameof(saslMechanism));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslUsername(string saslUsername) {
        Target.SaslUsername = saslUsername ?? throw new ArgumentNullException(nameof(saslUsername));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslPassword(string saslPassword) {
        Target.SaslPassword = saslPassword ?? throw new ArgumentNullException(nameof(saslPassword));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCaLocation(string sslCaLocation) {
        Target.SslCaLocation = sslCaLocation ?? throw new ArgumentNullException(nameof(sslCaLocation));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SecurityProtocol(SecurityProtocol? securityProtocol) {
        Target.SecurityProtocol = securityProtocol ?? throw new ArgumentNullException(nameof(securityProtocol));
        return this;
    }

    public KafkaMessageBusOptionsBuilder AutoCommitIntervalMs(int? autoCommitIntervalMs) {
        Target.AutoCommitIntervalMs = autoCommitIntervalMs ?? throw new ArgumentNullException(nameof(autoCommitIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder NumberOfPartitions(int? numberOfPartitions) {
        Target.TopicNumberOfPartitions = numberOfPartitions ?? throw new ArgumentNullException(nameof(numberOfPartitions));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ReplicationFactor(short? replicationFactor) {
        Target.TopicReplicationFactor = replicationFactor ?? throw new ArgumentNullException(nameof(replicationFactor));
        return this;
    }

    public KafkaMessageBusOptionsBuilder Acks(Acks? acks) {
        Target.Acks = acks ?? throw new ArgumentNullException(nameof(acks));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ClientId(string clientId) {
        Target.ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MessageMaxBytes(int? messageMaxBytes) {
        Target.MessageMaxBytes = messageMaxBytes ?? throw new ArgumentNullException(nameof(messageMaxBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MessageCopyMaxBytes(int? messageCopyMaxBytes) {
        Target.MessageCopyMaxBytes = messageCopyMaxBytes ?? throw new ArgumentNullException(nameof(messageCopyMaxBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ReceiveMessageMaxBytes(int? receiveMessageMaxBytes) {
        Target.ReceiveMessageMaxBytes = receiveMessageMaxBytes ?? throw new ArgumentNullException(nameof(receiveMessageMaxBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MaxInFlight(int? maxInFlight) {
        Target.MaxInFlight = maxInFlight ?? throw new ArgumentNullException(nameof(maxInFlight));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicConfigs(Dictionary<string, string> topicConfigs) {
        Target.TopicConfigs = topicConfigs ?? throw new ArgumentNullException(nameof(topicConfigs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicReplicasAssignments(Dictionary<int, List<int>> topicReplicasAssignments) {
        Target.TopicReplicasAssignments = topicReplicasAssignments ?? throw new ArgumentNullException(nameof(topicReplicasAssignments));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicMetadataRefreshIntervalMs(int? topicMetadataRefreshIntervalMs) {
        Target.TopicMetadataRefreshIntervalMs = topicMetadataRefreshIntervalMs ?? throw new ArgumentNullException(nameof(topicMetadataRefreshIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MetadataMaxAgeMs(int? metadataMaxAgeMs) {
        Target.MetadataMaxAgeMs = metadataMaxAgeMs ?? throw new ArgumentNullException(nameof(metadataMaxAgeMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicMetadataRefreshFastIntervalMs(int? topicMetadataRefreshFastIntervalMs) {
        Target.TopicMetadataRefreshFastIntervalMs = topicMetadataRefreshFastIntervalMs ?? throw new ArgumentNullException(nameof(topicMetadataRefreshFastIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicMetadataRefreshSparse(bool? topicMetadataRefreshSparse) {
        Target.TopicMetadataRefreshSparse = topicMetadataRefreshSparse ?? throw new ArgumentNullException(nameof(topicMetadataRefreshSparse));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicMetadataPropagationMaxMs(int? topicMetadataPropagationMaxMs) {
        Target.TopicMetadataPropagationMaxMs = topicMetadataPropagationMaxMs ?? throw new ArgumentNullException(nameof(topicMetadataPropagationMaxMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TopicBlacklist(string topicBlacklist) {
        Target.TopicBlacklist = topicBlacklist ?? throw new ArgumentNullException(nameof(topicBlacklist));
        return this;
    }

    public KafkaMessageBusOptionsBuilder Debug(string debug) {
        Target.Debug = debug ?? throw new ArgumentNullException(nameof(debug));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SocketTimeoutMs(int? socketTimeoutMs) {
        Target.SocketTimeoutMs = socketTimeoutMs ?? throw new ArgumentNullException(nameof(socketTimeoutMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SocketSendBufferBytes(int? socketSendBufferBytes) {
        Target.SocketSendBufferBytes = socketSendBufferBytes ?? throw new ArgumentNullException(nameof(socketSendBufferBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SocketReceiveBufferBytes(int? socketReceiveBufferBytes) {
        Target.SocketReceiveBufferBytes = socketReceiveBufferBytes ?? throw new ArgumentNullException(nameof(socketReceiveBufferBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SocketKeepaliveEnable(bool? socketKeepaliveEnable) {
        Target.SocketKeepaliveEnable = socketKeepaliveEnable ?? throw new ArgumentNullException(nameof(socketKeepaliveEnable));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SocketNagleDisable(bool? socketNagleDisable) {
        Target.SocketNagleDisable = socketNagleDisable ?? throw new ArgumentNullException(nameof(socketNagleDisable));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SocketMaxFails(int? socketMaxFails) {
        Target.SocketMaxFails = socketMaxFails ?? throw new ArgumentNullException(nameof(socketMaxFails));
        return this;
    }

    public KafkaMessageBusOptionsBuilder BrokerAddressTtl(int? brokerAddressTtl) {
        Target.BrokerAddressTtl = brokerAddressTtl ?? throw new ArgumentNullException(nameof(brokerAddressTtl));
        return this;
    }

    public KafkaMessageBusOptionsBuilder BrokerAddressFamily(BrokerAddressFamily? brokerAddressFamily) {
        Target.BrokerAddressFamily = brokerAddressFamily ?? throw new ArgumentNullException(nameof(brokerAddressFamily));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ConnectionsMaxIdleMs(int? connectionsMaxIdleMs) {
        Target.ConnectionsMaxIdleMs = connectionsMaxIdleMs ?? throw new ArgumentNullException(nameof(connectionsMaxIdleMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ReconnectBackoffMs(int? reconnectBackoffMs) {
        Target.ReconnectBackoffMs = reconnectBackoffMs ?? throw new ArgumentNullException(nameof(reconnectBackoffMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ReconnectBackoffMaxMs(int? reconnectBackoffMaxMs) {
        Target.ReconnectBackoffMaxMs = reconnectBackoffMaxMs ?? throw new ArgumentNullException(nameof(reconnectBackoffMaxMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder StatisticsIntervalMs(int? statisticsIntervalMs) {
        Target.StatisticsIntervalMs = statisticsIntervalMs ?? throw new ArgumentNullException(nameof(statisticsIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder LogQueue(bool? logQueue) {
        Target.LogQueue = logQueue ?? throw new ArgumentNullException(nameof(logQueue));
        return this;
    }

    public KafkaMessageBusOptionsBuilder LogThreadName(bool? logThreadName) {
        Target.LogThreadName = logThreadName ?? throw new ArgumentNullException(nameof(logThreadName));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableRandomSeed(bool? enableRandomSeed) {
        Target.EnableRandomSeed = enableRandomSeed ?? throw new ArgumentNullException(nameof(enableRandomSeed));
        return this;
    }

    public KafkaMessageBusOptionsBuilder LogConnectionClose(bool? logConnectionClose) {
        Target.LogConnectionClose = logConnectionClose ?? throw new ArgumentNullException(nameof(logConnectionClose));
        return this;
    }

    public KafkaMessageBusOptionsBuilder InternalTerminationSignal(int? internalTerminationSignal) {
        Target.InternalTerminationSignal = internalTerminationSignal ?? throw new ArgumentNullException(nameof(internalTerminationSignal));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ApiVersionRequest(bool? apiVersionRequest) {
        Target.ApiVersionRequest = apiVersionRequest ?? throw new ArgumentNullException(nameof(apiVersionRequest));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ApiVersionRequestTimeoutMs(int? apiVersionRequestTimeoutMs) {
        Target.ApiVersionRequestTimeoutMs = apiVersionRequestTimeoutMs ?? throw new ArgumentNullException(nameof(apiVersionRequestTimeoutMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ApiVersionFallbackMs(int? apiVersionFallbackMs) {
        Target.ApiVersionFallbackMs = apiVersionFallbackMs ?? throw new ArgumentNullException(nameof(apiVersionFallbackMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder BrokerVersionFallback(string brokerVersionFallback) {
        Target.BrokerVersionFallback = brokerVersionFallback ?? throw new ArgumentNullException(nameof(brokerVersionFallback));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCipherSuites(string sslCipherSuites) {
        Target.SslCipherSuites = sslCipherSuites ?? throw new ArgumentNullException(nameof(sslCipherSuites));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCurvesList(string sslCurvesList) {
        Target.SslCurvesList = sslCurvesList ?? throw new ArgumentNullException(nameof(sslCurvesList));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslSigalgsList(string sslSigalgsList) {
        Target.SslSigalgsList = sslSigalgsList ?? throw new ArgumentNullException(nameof(sslSigalgsList));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslKeyLocation(string sslKeyLocation) {
        Target.SslKeyLocation = sslKeyLocation ?? throw new ArgumentNullException(nameof(sslKeyLocation));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslKeyPassword(string sslKeyPassword) {
        Target.SslKeyPassword = sslKeyPassword ?? throw new ArgumentNullException(nameof(sslKeyPassword));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslKeyPem(string sslKeyPem) {
        Target.SslKeyPem = sslKeyPem ?? throw new ArgumentNullException(nameof(sslKeyPem));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCertificatePem(string sslCertificatePem) {
        Target.SslCertificatePem = sslCertificatePem ?? throw new ArgumentNullException(nameof(sslCertificatePem));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCaPem(string sslCaPem) {
        Target.SslCaPem = sslCaPem ?? throw new ArgumentNullException(nameof(sslCaPem));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCaCertificateStores(string sslCaCertificateStores) {
        Target.SslCaCertificateStores = sslCaCertificateStores ?? throw new ArgumentNullException(nameof(sslCaCertificateStores));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslCrlLocation(string sslCrlLocation) {
        Target.SslCrlLocation = sslCrlLocation ?? throw new ArgumentNullException(nameof(sslCrlLocation));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslKeystoreLocation(string sslKeystoreLocation) {
        Target.SslKeystoreLocation = sslKeystoreLocation ?? throw new ArgumentNullException(nameof(sslKeystoreLocation));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslKeystorePassword(string sslKeystorePassword) {
        Target.SslKeystorePassword = sslKeystorePassword ?? throw new ArgumentNullException(nameof(sslKeystorePassword));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslEngineLocation(string sslEngineLocation) {
        Target.SslEngineLocation = sslEngineLocation ?? throw new ArgumentNullException(nameof(sslEngineLocation));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslEngineId(string sslEngineId) {
        Target.SslEngineId = sslEngineId ?? throw new ArgumentNullException(nameof(sslEngineId));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableSslCertificateVerification(bool? enableSslCertificateVerification) {
        Target.EnableSslCertificateVerification = enableSslCertificateVerification ?? throw new ArgumentNullException(nameof(enableSslCertificateVerification));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SslEndpointIdentificationAlgorithm(SslEndpointIdentificationAlgorithm? sslEndpointIdentificationAlgorithm) {
        Target.SslEndpointIdentificationAlgorithm = sslEndpointIdentificationAlgorithm ?? throw new ArgumentNullException(nameof(sslEndpointIdentificationAlgorithm));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslKerberosServiceName(string saslKerberosServiceName) {
        Target.SaslKerberosServiceName = saslKerberosServiceName ?? throw new ArgumentNullException(nameof(saslKerberosServiceName));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslKerberosPrincipal(string saslKerberosPrincipal) {
        Target.SaslKerberosPrincipal = saslKerberosPrincipal ?? throw new ArgumentNullException(nameof(saslKerberosPrincipal));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslKerberosKinitCmd(string saslKerberosKinitCmd) {
        Target.SaslKerberosKinitCmd = saslKerberosKinitCmd ?? throw new ArgumentNullException(nameof(saslKerberosKinitCmd));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslKerberosKeytab(string saslKerberosKeytab) {
        Target.SaslKerberosKeytab = saslKerberosKeytab ?? throw new ArgumentNullException(nameof(saslKerberosKeytab));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslKerberosMinTimeBeforeRelogin(int? saslKerberosMinTimeBeforeRelogin) {
        Target.SaslKerberosMinTimeBeforeRelogin = saslKerberosMinTimeBeforeRelogin ?? throw new ArgumentNullException(nameof(saslKerberosMinTimeBeforeRelogin));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SaslOauthbearerConfig(string saslOauthbearerConfig) {
        Target.SaslOauthbearerConfig = saslOauthbearerConfig ?? throw new ArgumentNullException(nameof(saslOauthbearerConfig));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableSaslOauthbearerUnsecureJwt(bool? enableSaslOauthbearerUnsecureJwt) {
        Target.EnableSaslOauthbearerUnsecureJwt = enableSaslOauthbearerUnsecureJwt ?? throw new ArgumentNullException(nameof(enableSaslOauthbearerUnsecureJwt));
        return this;
    }

    public KafkaMessageBusOptionsBuilder PluginLibraryPaths(string pluginLibraryPaths) {
        Target.PluginLibraryPaths = pluginLibraryPaths ?? throw new ArgumentNullException(nameof(pluginLibraryPaths));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ClientRack(string clientRack) {
        Target.ClientRack = clientRack ?? throw new ArgumentNullException(nameof(clientRack));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableBackgroundPoll(bool? enableBackgroundPoll) {
        Target.EnableBackgroundPoll = enableBackgroundPoll ?? throw new ArgumentNullException(nameof(enableBackgroundPoll));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableDeliveryReports(bool? enableDeliveryReports) {
        Target.EnableDeliveryReports = enableDeliveryReports ?? throw new ArgumentNullException(nameof(enableDeliveryReports));
        return this;
    }

    public KafkaMessageBusOptionsBuilder DeliveryReportFields(string deliveryReportFields) {
        Target.DeliveryReportFields = deliveryReportFields ?? throw new ArgumentNullException(nameof(deliveryReportFields));
        return this;
    }

    public KafkaMessageBusOptionsBuilder RequestTimeoutMs(int? requestTimeoutMs) {
        Target.RequestTimeoutMs = requestTimeoutMs ?? throw new ArgumentNullException(nameof(requestTimeoutMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MessageTimeoutMs(int? messageTimeoutMs) {
        Target.MessageTimeoutMs = messageTimeoutMs ?? throw new ArgumentNullException(nameof(messageTimeoutMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder Partitioner(Partitioner? partitioner) {
        Target.Partitioner = partitioner ?? throw new ArgumentNullException(nameof(partitioner));
        return this;
    }

    public KafkaMessageBusOptionsBuilder CompressionLevel(int? compressionLevel) {
        Target.CompressionLevel = compressionLevel ?? throw new ArgumentNullException(nameof(compressionLevel));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TransactionalId(string transactionalId) {
        Target.TransactionalId = transactionalId ?? throw new ArgumentNullException(nameof(transactionalId));
        return this;
    }

    public KafkaMessageBusOptionsBuilder TransactionTimeoutMs(int? transactionTimeoutMs) {
        Target.TransactionTimeoutMs = transactionTimeoutMs ?? throw new ArgumentNullException(nameof(transactionTimeoutMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableIdempotence(bool? enableIdempotence) {
        Target.EnableIdempotence = enableIdempotence ?? throw new ArgumentNullException(nameof(enableIdempotence));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableGaplessGuarantee(bool? enableGaplessGuarantee) {
        Target.EnableGaplessGuarantee = enableGaplessGuarantee ?? throw new ArgumentNullException(nameof(enableGaplessGuarantee));
        return this;
    }

    public KafkaMessageBusOptionsBuilder QueueBufferingMaxMessages(int? queueBufferingMaxMessages) {
        Target.QueueBufferingMaxMessages = queueBufferingMaxMessages ?? throw new ArgumentNullException(nameof(queueBufferingMaxMessages));
        return this;
    }

    public KafkaMessageBusOptionsBuilder QueueBufferingMaxKbytes(int? queueBufferingMaxKbytes) {
        Target.QueueBufferingMaxKbytes = queueBufferingMaxKbytes ?? throw new ArgumentNullException(nameof(queueBufferingMaxKbytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder LingerMs(double? lingerMs) {
        Target.LingerMs = lingerMs ?? throw new ArgumentNullException(nameof(lingerMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MessageSendMaxRetries(int? messageSendMaxRetries) {
        Target.MessageSendMaxRetries = messageSendMaxRetries ?? throw new ArgumentNullException(nameof(messageSendMaxRetries));
        return this;
    }

    public KafkaMessageBusOptionsBuilder RetryBackoffMs(int? retryBackoffMs) {
        Target.RetryBackoffMs = retryBackoffMs ?? throw new ArgumentNullException(nameof(retryBackoffMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder QueueBufferingBackpressureThreshold(int? queueBufferingBackpressureThreshold) {
        Target.QueueBufferingBackpressureThreshold = queueBufferingBackpressureThreshold ?? throw new ArgumentNullException(nameof(queueBufferingBackpressureThreshold));
        return this;
    }

    public KafkaMessageBusOptionsBuilder CompressionType(CompressionType? compressionType) {
        Target.CompressionType = compressionType ?? throw new ArgumentNullException(nameof(compressionType));
        return this;
    }

    public KafkaMessageBusOptionsBuilder BatchNumMessages(int? batchNumMessages) {
        Target.BatchNumMessages = batchNumMessages ?? throw new ArgumentNullException(nameof(batchNumMessages));
        return this;
    }

    public KafkaMessageBusOptionsBuilder BatchSize(int? batchSize) {
        Target.BatchSize = batchSize ?? throw new ArgumentNullException(nameof(batchSize));
        return this;
    }

    public KafkaMessageBusOptionsBuilder StickyPartitioningLingerMs(int? stickyPartitioningLingerMs) {
        Target.StickyPartitioningLingerMs = stickyPartitioningLingerMs ?? throw new ArgumentNullException(nameof(stickyPartitioningLingerMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder ConsumeResultFields(string consumeResultFields) {
        Target.ConsumeResultFields = consumeResultFields ?? throw new ArgumentNullException(nameof(consumeResultFields));
        return this;
    }

    public KafkaMessageBusOptionsBuilder AutoOffsetReset(AutoOffsetReset? autoOffsetReset) {
        Target.AutoOffsetReset = autoOffsetReset ?? throw new ArgumentNullException(nameof(autoOffsetReset));
        return this;
    }

    public KafkaMessageBusOptionsBuilder GroupInstanceId(string groupInstanceId) {
        Target.GroupInstanceId = groupInstanceId ?? throw new ArgumentNullException(nameof(groupInstanceId));
        return this;
    }

    public KafkaMessageBusOptionsBuilder PartitionAssignmentStrategy(PartitionAssignmentStrategy? partitionAssignmentStrategy) {
        Target.PartitionAssignmentStrategy = partitionAssignmentStrategy ?? throw new ArgumentNullException(nameof(partitionAssignmentStrategy));
        return this;
    }

    public KafkaMessageBusOptionsBuilder SessionTimeoutMs(int? sessionTimeoutMs) {
        Target.SessionTimeoutMs = sessionTimeoutMs ?? throw new ArgumentNullException(nameof(sessionTimeoutMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder HeartbeatIntervalMs(int? heartbeatIntervalMs) {
        Target.HeartbeatIntervalMs = heartbeatIntervalMs ?? throw new ArgumentNullException(nameof(heartbeatIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder GroupProtocolType(string groupProtocolType) {
        Target.GroupProtocolType = groupProtocolType ?? throw new ArgumentNullException(nameof(groupProtocolType));
        return this;
    }

    public KafkaMessageBusOptionsBuilder CoordinatorQueryIntervalMs(int? coordinatorQueryIntervalMs) {
        Target.CoordinatorQueryIntervalMs = coordinatorQueryIntervalMs ?? throw new ArgumentNullException(nameof(coordinatorQueryIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MaxPollIntervalMs(int? maxPollIntervalMs) {
        Target.MaxPollIntervalMs = maxPollIntervalMs ?? throw new ArgumentNullException(nameof(maxPollIntervalMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableAutoCommit(bool? enableAutoCommit) {
        Target.EnableAutoCommit = enableAutoCommit ?? throw new ArgumentNullException(nameof(enableAutoCommit));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnableAutoOffsetStore(bool? enableAutoOffsetStore) {
        Target.EnableAutoOffsetStore = enableAutoOffsetStore ?? throw new ArgumentNullException(nameof(enableAutoOffsetStore));
        return this;
    }

    public KafkaMessageBusOptionsBuilder QueuedMinMessages(int? queuedMinMessages) {
        Target.QueuedMinMessages = queuedMinMessages ?? throw new ArgumentNullException(nameof(queuedMinMessages));
        return this;
    }

    public KafkaMessageBusOptionsBuilder QueuedMaxMessagesKbytes(int? queuedMaxMessagesKbytes) {
        Target.QueuedMaxMessagesKbytes = queuedMaxMessagesKbytes ?? throw new ArgumentNullException(nameof(queuedMaxMessagesKbytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder FetchErrorBackoffMs(int? fetchErrorBackoffMs) {
        Target.FetchErrorBackoffMs = fetchErrorBackoffMs ?? throw new ArgumentNullException(nameof(fetchErrorBackoffMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder FetchWaitMaxMs(int? fetchWaitMaxMs) {
        Target.FetchWaitMaxMs = fetchWaitMaxMs ?? throw new ArgumentNullException(nameof(fetchWaitMaxMs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder MaxPartitionFetchBytes(int? maxPartitionFetchBytes) {
        Target.MaxPartitionFetchBytes = maxPartitionFetchBytes ?? throw new ArgumentNullException(nameof(maxPartitionFetchBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder FetchMaxBytes(int? fetchMaxBytes) {
        Target.FetchMaxBytes = fetchMaxBytes ?? throw new ArgumentNullException(nameof(fetchMaxBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder FetchMinBytes(int? fetchMinBytes) {
        Target.FetchMinBytes = fetchMinBytes ?? throw new ArgumentNullException(nameof(fetchMinBytes));
        return this;
    }

    public KafkaMessageBusOptionsBuilder IsolationLevel(IsolationLevel? isolationLevel) {
        Target.IsolationLevel = isolationLevel ?? throw new ArgumentNullException(nameof(isolationLevel));
        return this;
    }

    public KafkaMessageBusOptionsBuilder EnablePartitionEof(bool? enablePartitionEof) {
        Target.EnablePartitionEof = enablePartitionEof ?? throw new ArgumentNullException(nameof(enablePartitionEof));
        return this;
    }

    public KafkaMessageBusOptionsBuilder CheckCrcs(bool? checkCrcs) {
        Target.CheckCrcs = checkCrcs ?? throw new ArgumentNullException(nameof(checkCrcs));
        return this;
    }

    public KafkaMessageBusOptionsBuilder AllowAutoCreateTopics(bool? allowAutoCreateTopics) {
        Target.AllowAutoCreateTopics = allowAutoCreateTopics ?? throw new ArgumentNullException(nameof(allowAutoCreateTopics));
        return this;
    }
}