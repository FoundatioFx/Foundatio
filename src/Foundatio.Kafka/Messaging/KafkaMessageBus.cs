using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

public class KafkaMessageBus : MessageBusBase<KafkaMessageBusOptions> {
    private bool _isDisposed;
    private readonly CancellationTokenSource _messageBusDisposedCancellationTokenSource = new();
    private Task _listeningTask;
    private readonly AdminClientConfig _adminClientConfig;
    private readonly ProducerConfig _producerConfig;
    private readonly ConsumerConfig _consumerConfig;
    private readonly IProducer<string, KafkaMessageEnvelope> _producer;
    private readonly AsyncLock _lock = new();

    public KafkaMessageBus(KafkaMessageBusOptions options) : base(options) {
        _adminClientConfig = CreateAdminConfig();
        _consumerConfig = CreateConsumerConfig();
        _producerConfig = CreateProducerConfig();
        _producer = new ProducerBuilder<string, KafkaMessageEnvelope>(_producerConfig).SetValueSerializer(new KafkaSerializer(_serializer)).Build();
    }

    public KafkaMessageBus(Builder<KafkaMessageBusOptionsBuilder, KafkaMessageBusOptions> config)
        : this(config(new KafkaMessageBusOptionsBuilder()).Build()) {
    }
 
    protected override Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("PublishImplAsync([{messageType}])", messageType);
        ///Get rid of envelope (short type and header)
        var kafkaMessage = new KafkaMessageEnvelope {
            Type = messageType,
            Data = SerializeMessageBody(messageType, message)
        };

        _producer.Produce(_options.TopicName, new Message<string, KafkaMessageEnvelope> { Key = messageType, Value = kafkaMessage },
        (deliveryReport) => {
            ///rethrow error and logging ? rabbitmq
            if (deliveryReport.Error.Code != ErrorCode.NoError) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Failed to deliver message: {Reason}", deliveryReport.Error.Reason);
            } else {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Produced message to: {TopicPartitionOffset}", deliveryReport.TopicPartitionOffset);
            }
        });
        return Task.CompletedTask;
    }

    private async Task OnMessageAsync<TKey, TValue>(ConsumeResult<TKey, TValue> consumeResult) where TValue : KafkaMessageEnvelope {
        if (_subscribers.IsEmpty)
            return;
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("OnMessage( topic: {Topic},groupId: {GroupId} partition: [{Partition}] offset: [{Offset}] partitionOffset; [{TopicPartitionOffset}])", consumeResult.Topic,_consumerConfig.GroupId, consumeResult.Partition, consumeResult.Offset, consumeResult.TopicPartitionOffset);

        IMessage message;
        try {
            message = ConvertToMessage(consumeResult.Message.Value);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "OnMessage({Offset}] {Partition}) Error deserializing message: {Message}", consumeResult.Offset, consumeResult.TopicPartition.Partition, ex.Message);
            return;
        }

        await SendMessageToSubscribersAsync(message).AnyContext();
    }

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
        // Check if we create topic here / check redis / rabbit implementation, we cache the result under isSubscribed and use an async lock.
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("EnsureTopicSubscriptionAsync");
        await EnsureTopicCreatedAsync();
        EnsureListening();
    }

    protected virtual IMessage ConvertToMessage(KafkaMessageEnvelope envelope) {
        return new Message(() => DeserializeMessageBody(envelope.Type, envelope.Data)) {
            Type = envelope.Type,
            ClrType = GetMappedMessageType(envelope.Type),
            Data = envelope.Data
        };
    }

    private void EnsureListening() {
        if (_listeningTask is not null) {
            _logger.LogDebug("StartListening: Already listening");
            return;
        }

        _listeningTask = Task.Run(async () => {
            using var consumer = new ConsumerBuilder<string, KafkaMessageEnvelope>(_consumerConfig).SetValueDeserializer(new KafkaSerializer(_serializer)).Build();
            consumer.Subscribe(_options.TopicName);
            _logger.LogInformation("EnsureListening consumer {Name} subscribed on {Topic}", consumer.Name, _options.Topic);

            try {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("MessageBus {MessageBusId} dispose", MessageBusId);

                while (!_messageBusDisposedCancellationTokenSource.IsCancellationRequested) {
                    var consumeResult = consumer.Consume(_messageBusDisposedCancellationTokenSource.Token);

                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Consumed topic: {consumeResult.Topic} by consumer : {consumer.Name} partition {consumeResult.TopicPartition}");
                    await OnMessageAsync(consumeResult).AnyContext();
                }
            } catch (OperationCanceledException) {
                consumer.Unsubscribe();
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Error consuming message: {Message}", ex.Message);
            } finally {
                consumer.Close();
            }
        }, _messageBusDisposedCancellationTokenSource.Token);
    }

    public override void Dispose() {
        if (_isDisposed) {
            _logger.LogTrace("MessageBus {MessageBusId} dispose was already called", MessageBusId);
            return;
        }
        _isDisposed = true;

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("MessageBus {MessageBusId} dispose", MessageBusId);

        int? queueSize = _producer?.Flush(TimeSpan.FromSeconds(15));
        if (queueSize > 0) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Flushing producer {queueSize}", queueSize);
        }
        _producer?.Dispose();

        _messageBusDisposedCancellationTokenSource.Cancel();
        _messageBusDisposedCancellationTokenSource.Dispose();
        base.Dispose();
    }

    private async Task EnsureTopicCreatedAsync() {
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("EnsureTopicCreatedAsync {Topic}", _options.Topic);
        using (await _lock.LockAsync().AnyContext()) {
            using var adminClient = new AdminClientBuilder(_adminClientConfig).Build();
            try {
                var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(2));
                bool isTopicExist = metadata.Topics.Any(t => t.Topic == _options.TopicName);
                if (!isTopicExist)
                    await adminClient.CreateTopicsAsync(new TopicSpecification[] { new TopicSpecification { Name = _options.TopicName, ReplicationFactor = _options.TopicReplicationFactor, NumPartitions = _options.TopicNumberOfPartitions, Configs = _options.TopicConfigs, ReplicasAssignments = _options.TopicReplicasAssignments } });
                ///Logging and rethrow
            } catch (CreateTopicsException e) {
                if (e.Results[0].Error.Code != ErrorCode.TopicAlreadyExists) {
                    if (_logger.IsEnabled(LogLevel.Trace)) 
                        _logger.LogTrace("An error occured creating topic {Topic}: {Reason}", _options.Topic, e.Results[0].Error.Reason);

                } else {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Topic {Topic} already exists", _options.Topic);
                }
            }
        }
    }

    private ClientConfig CreateClientConfig() {
        return new ClientConfig {
            SaslMechanism = _options.SaslMechanism,
            Acks = _options.Acks,
            ClientId = _options.ClientId,
            BootstrapServers = _options.BootstrapServers,
            MessageMaxBytes = _options.MessageMaxBytes,
            MessageCopyMaxBytes = _options.MessageCopyMaxBytes,
            ReceiveMessageMaxBytes = _options.ReceiveMessageMaxBytes,
            MaxInFlight = _options.MaxInFlight,
            TopicMetadataRefreshIntervalMs = _options.TopicMetadataRefreshIntervalMs,
            MetadataMaxAgeMs = _options.MetadataMaxAgeMs,
            TopicMetadataRefreshFastIntervalMs = _options.TopicMetadataRefreshFastIntervalMs,
            TopicMetadataRefreshSparse = _options.TopicMetadataRefreshSparse,
            TopicMetadataPropagationMaxMs = _options.TopicMetadataPropagationMaxMs,
            TopicBlacklist = _options.TopicBlacklist,
            Debug = _options.Debug,
            SocketTimeoutMs = _options.SocketTimeoutMs,
            SocketSendBufferBytes = _options.SocketSendBufferBytes,
            SocketReceiveBufferBytes = _options.SocketReceiveBufferBytes,
            SocketKeepaliveEnable = _options.SocketKeepaliveEnable,
            SocketNagleDisable = _options.SocketNagleDisable,
            SocketMaxFails = _options.SocketMaxFails,
            BrokerAddressTtl = _options.BrokerAddressTtl,
            BrokerAddressFamily = _options.BrokerAddressFamily,
            ConnectionsMaxIdleMs = _options.ConnectionsMaxIdleMs,
            ReconnectBackoffMs = _options.ReconnectBackoffMs,
            ReconnectBackoffMaxMs = _options.ReconnectBackoffMaxMs,
            StatisticsIntervalMs = _options.StatisticsIntervalMs,
            LogQueue = _options.LogQueue,
            LogThreadName = _options.LogThreadName,
            EnableRandomSeed = _options.EnableRandomSeed,
            LogConnectionClose = _options.LogConnectionClose,
            InternalTerminationSignal = _options.InternalTerminationSignal,
            ApiVersionRequest = _options.ApiVersionRequest,
            ApiVersionRequestTimeoutMs = _options.ApiVersionRequestTimeoutMs,
            ApiVersionFallbackMs = _options.ApiVersionFallbackMs,
            BrokerVersionFallback = _options.BrokerVersionFallback,
            SecurityProtocol = _options.SecurityProtocol,
            SslCipherSuites = _options.SslCipherSuites,
            SslCurvesList = _options.SslCurvesList,
            SslSigalgsList = _options.SslSigalgsList,
            SslKeyLocation = _options.SslKeyLocation,
            SslKeyPassword = _options.SslKeyPassword,
            SslKeyPem = _options.SslKeyPem,
            SslCertificateLocation = _options.SslCertificateLocation,
            SslCertificatePem = _options.SslCertificatePem,
            SslCaLocation = _options.SslCaLocation,
            SslCaPem = _options.SslCaPem,
            SslCaCertificateStores = _options.SslCaCertificateStores,
            SslCrlLocation = _options.SslCrlLocation,
            SslKeystoreLocation = _options.SslKeystoreLocation,
            SslKeystorePassword = _options.SslKeystorePassword,
            SslEngineLocation = _options.SslEngineLocation,
            SslEngineId = _options.SslEngineId,
            EnableSslCertificateVerification = _options.EnableSslCertificateVerification,
            SslEndpointIdentificationAlgorithm = _options.SslEndpointIdentificationAlgorithm,
            SaslKerberosServiceName = _options.SaslKerberosServiceName,
            SaslKerberosPrincipal = _options.SaslKerberosPrincipal,
            SaslKerberosKinitCmd = _options.SaslKerberosKinitCmd,
            SaslKerberosKeytab = _options.SaslKerberosKeytab,
            SaslKerberosMinTimeBeforeRelogin = _options.SaslKerberosMinTimeBeforeRelogin,
            SaslUsername = _options.SaslUsername,
            SaslPassword = _options.SaslPassword,
            SaslOauthbearerConfig = _options.SaslOauthbearerConfig,
            EnableSaslOauthbearerUnsecureJwt = _options.EnableSaslOauthbearerUnsecureJwt,
            PluginLibraryPaths = _options.PluginLibraryPaths,
            ClientRack = _options.ClientRack,
        };
    }

    private AdminClientConfig CreateAdminConfig() {
        var _clientConfig = CreateClientConfig();
        var config = new AdminClientConfig(_clientConfig);
        return config;
    }

    /// <summary>
    /// check default values
    /// </summary>
    /// <returns></returns>
    private ProducerConfig CreateProducerConfig() {
        var _clientConfig = CreateClientConfig();
        var config = new ProducerConfig(_clientConfig) {
            EnableBackgroundPoll = _options.EnableBackgroundPoll,
            EnableDeliveryReports = _options.EnableDeliveryReports,
            RequestTimeoutMs = _options.RequestTimeoutMs,
            MessageTimeoutMs = _options.MessageTimeoutMs,
            Partitioner = _options.Partitioner,
            CompressionLevel = _options.CompressionLevel,
            TransactionalId = _options.TransactionalId,
            TransactionTimeoutMs = _options.TransactionTimeoutMs,
            EnableIdempotence = _options.EnableIdempotence,
            EnableGaplessGuarantee = _options.EnableGaplessGuarantee,
            QueueBufferingMaxMessages = _options.QueueBufferingMaxMessages,
            QueueBufferingMaxKbytes = _options.QueueBufferingMaxKbytes,
            LingerMs = _options.LingerMs,
            MessageSendMaxRetries = _options.MessageSendMaxRetries,
            RetryBackoffMs = _options.RetryBackoffMs,
            QueueBufferingBackpressureThreshold = _options.QueueBufferingBackpressureThreshold,
            CompressionType = _options.CompressionType,
            BatchNumMessages = _options.BatchNumMessages,
            BatchSize = _options.BatchSize,
            StickyPartitioningLingerMs = _options.StickyPartitioningLingerMs
        };
        return config;
    }

    private ConsumerConfig CreateConsumerConfig() {
        var _clientConfig = CreateClientConfig();
        var config = new ConsumerConfig(_clientConfig) {
             ConsumeResultFields = _options.ConsumeResultFields,
             AutoOffsetReset = _options.AutoOffsetReset,
             GroupId = _options.GroupId,
             GroupInstanceId = _options.GroupInstanceId,
             PartitionAssignmentStrategy = _options.PartitionAssignmentStrategy,
             SessionTimeoutMs = _options.SessionTimeoutMs,
             HeartbeatIntervalMs = _options.HeartbeatIntervalMs,
             GroupProtocolType = _options.GroupProtocolType,
             CoordinatorQueryIntervalMs = _options.CoordinatorQueryIntervalMs,
             MaxPollIntervalMs = _options.MaxPollIntervalMs,
             EnableAutoCommit = _options.EnableAutoCommit,
             AutoCommitIntervalMs = _options.AutoCommitIntervalMs,
             EnableAutoOffsetStore = _options.EnableAutoOffsetStore,
             QueuedMinMessages = _options.QueuedMinMessages,
             QueuedMaxMessagesKbytes = _options.QueuedMaxMessagesKbytes,
             FetchWaitMaxMs = _options.FetchWaitMaxMs,
             MaxPartitionFetchBytes = _options.MaxPartitionFetchBytes,
             FetchMaxBytes = _options.FetchMaxBytes,
             FetchMinBytes = _options.FetchMinBytes,
             FetchErrorBackoffMs = _options.FetchErrorBackoffMs,
             IsolationLevel = _options.IsolationLevel,
             EnablePartitionEof = _options.EnablePartitionEof,
             CheckCrcs = _options.CheckCrcs,
             AllowAutoCreateTopics = _options.AllowAutoCreateTopics
        };
        return config;
    }

    public class KafkaMessageEnvelope {
        public string Type { get; set; }
        public byte[] Data { get; set; }
    }

    public class KafkaSerializer : Confluent.Kafka.ISerializer<KafkaMessageEnvelope>, IDeserializer<KafkaMessageEnvelope> {
        private readonly ISerializer _serializer;

        public KafkaSerializer(ISerializer serializer) {
            _serializer = serializer;
        }

        public byte[] Serialize(KafkaMessageEnvelope data, SerializationContext context) {
            return _serializer.SerializeToBytes(data);
        }

        public KafkaMessageEnvelope Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context) {
            using var stream = new MemoryStream(data.ToArray());
            return _serializer.Deserialize<KafkaMessageEnvelope>(stream);
        }
    }
}