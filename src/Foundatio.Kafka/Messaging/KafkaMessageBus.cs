using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

public class KafkaMessageBus : MessageBusBase<KafkaMessageBusOptions> {
    private bool _isDisposed;
    private readonly CancellationTokenSource _messageBusDisposedCancellationTokenSource = new();
    private Task _listeningTask;
    private readonly AdminClientConfig _adminClientConfig;
    private readonly ProducerConfig _producerConfig;
    private readonly ConsumerConfig _consumerConfig;
    private readonly IProducer<string, byte[]> _producer;
    private readonly AsyncLock _lock = new();
    private const string MessageType = "MessageType";
    private const string ContentType = "ContentType";
    private const string CorrelationId = "CorrelationId";

    public KafkaMessageBus(KafkaMessageBusOptions options) : base(options) {
        _adminClientConfig = CreateAdminConfig();
        _consumerConfig = CreateConsumerConfig();
        _producerConfig = CreateProducerConfig();
        _producer = new ProducerBuilder<string, byte[]>(_producerConfig).SetLogHandler((client, message) => OnKafkaClientMessage(client, message)).Build();
    }

    public KafkaMessageBus(Builder<KafkaMessageBusOptionsBuilder, KafkaMessageBusOptions> config)
        : this(config(new KafkaMessageBusOptionsBuilder()).Build()) {
    }

    protected override Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("PublishImplAsync([{MessageType}])", messageType);
        var headers = new Headers();
        headers.Add(new Header(MessageType, Encoding.UTF8.GetBytes(messageType)));
        headers.Add(new Header(ContentType, Encoding.UTF8.GetBytes(_options.ContentType)));
        if (options?.CorrelationId != null)
            headers.Add(new Header(CorrelationId, Encoding.UTF8.GetBytes(options.CorrelationId)));

        var publishMessage = new Message<string, byte[]> {
            Key = _options.PublishKey,
            Value = SerializeMessageBody(messageType, message),
            Headers = headers
        };

        _producer.Produce(_options.TopicName, publishMessage,
        (deliveryReport) => {
            if (deliveryReport.Error.Code != ErrorCode.NoError) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Failed to deliver message: {Reason}", deliveryReport.Error.Reason);
            } else {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Produced message to: {TopicPartitionOffset}", deliveryReport.TopicPartitionOffset);
            }
        });
        return Task.CompletedTask;
    }
    private async Task OnMessageAsync(ConsumeResult<string, byte[]> consumeResult) {
        if (_subscribers.IsEmpty)
            return;
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("OnMessage( topic: {Topic},groupId: {GroupId} partition: [{Partition}] offset: [{Offset}] partitionOffset; [{TopicPartitionOffset}])", consumeResult.Topic, _consumerConfig.GroupId, consumeResult.Partition, consumeResult.Offset, consumeResult.TopicPartitionOffset);
        IMessage message;
        try {
            message = ConvertToMessage(Encoding.UTF8.GetString(consumeResult.Message.Headers.SingleOrDefault(x => x.Key.Equals(MessageType)).GetValueBytes()), consumeResult.Message.Value);
            await SendMessageToSubscribersAsync(message).AnyContext();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "OnMessage({Offset}] {Partition}) Error deserializing message: {Message}", consumeResult.Offset, consumeResult.TopicPartition.Partition, ex.Message);
            return;
        }
    }

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("EnsureTopicSubscriptionAsync");
        await EnsureTopicCreatedAsync();
        EnsureListening();
    }
    protected virtual IMessage ConvertToMessage(string messageType, byte[] data) {
        return new Message(() => DeserializeMessageBody(messageType, data)) {
            Type = messageType,
            ClrType = GetMappedMessageType(messageType),
            Data = data
        };
    }
    private void EnsureListening() {
        if (_messageBusDisposedCancellationTokenSource.IsCancellationRequested)
            return;
        if (_listeningTask is { Status: TaskStatus.Running }) {
            _logger.LogDebug("Already Listening: {TopicName}", _options.TopicName);
            return;
        }

        _logger.LogDebug("Start Listening: {TopicName}", _options.TopicName);
        _listeningTask = Task.Run(async () => {
            using var consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig).SetLogHandler((client, message) => OnKafkaClientMessage(client, message)).Build();
            consumer.Subscribe(_options.TopicName);
            _logger.LogInformation("EnsureListening consumer {Name} subscribed on {Topic}", consumer.Name, _options.Topic);
            try {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("MessageBus {MessageBusId} dispose", MessageBusId);
                while (!_messageBusDisposedCancellationTokenSource.IsCancellationRequested) {
                    var consumeResult = consumer.Consume(_messageBusDisposedCancellationTokenSource.Token);
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace($"Consumed topic: {consumeResult.Topic} by consumer : {consumer.Name} partition {consumeResult.TopicPartition}");
                    await OnMessageAsync(consumeResult).AnyContext();
                }
            } catch (OperationCanceledException) {
                consumer.Unsubscribe();
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Error consuming message: {Message}", ex.Message);
            } finally {
                consumer.Close();
                _logger.LogDebug("Stop Listening {TopicName}", _options.TopicName);
            }
        }, _messageBusDisposedCancellationTokenSource.Token);
    }

    public override void Dispose() {
        if (_isDisposed) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("MessageBus {MessageBusId} dispose was already called", MessageBusId);
            return;
        }
        _isDisposed = true;
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("MessageBus {MessageBusId} dispose", MessageBusId);
        int? queueSize = _producer?.Flush(TimeSpan.FromSeconds(15));
        if (queueSize > 0)
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Flushing producer {queueSize}", queueSize);
        _producer?.Dispose();
        _messageBusDisposedCancellationTokenSource.Cancel();
        _messageBusDisposedCancellationTokenSource.Dispose();
        base.Dispose();
    }
    private void OnKafkaClientMessage(IClient client, LogMessage message) {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("[{LogLevel}] Client {Name} Message :{Message}", message.Level, client.Name, message.Message);
    }

    private async Task EnsureTopicCreatedAsync() {
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("EnsureTopicCreatedAsync {Topic}", _options.Topic);
        using (await _lock.LockAsync().AnyContext()) {
            using var adminClient = new AdminClientBuilder(_adminClientConfig).SetLogHandler((client, message) => OnKafkaClientMessage(client, message)).Build();
            try {
                var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(2));
                bool isTopicExist = metadata.Topics.Any(t => t.Topic == _options.TopicName);
                if (!isTopicExist) {
                    if (_options.AllowAutoCreateTopics.GetValueOrDefault())
                        await adminClient.CreateTopicsAsync(new TopicSpecification[] {
                           new TopicSpecification {
                               Name = _options.TopicName,
                               ReplicationFactor = _options.TopicReplicationFactor,
                               NumPartitions = _options.TopicNumberOfPartitions,
                               Configs = _options.TopicConfigs,
                               ReplicasAssignments = _options.TopicReplicasAssignments
                           }
                       });
                    else
                        throw new CreateTopicsException(new List<CreateTopicReport> {
                           new CreateTopicReport { Error = new Error(ErrorCode.TopicException, "Topic doesn't exist"), Topic = _options.TopicName }
                       });
                }
            } catch (CreateTopicsException ex) when (ex.Results[0].Error.Code is ErrorCode.TopicAlreadyExists) {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Topic {Topic} already exists", _options.Topic);
            } catch (CreateTopicsException ex) {
                _logger.LogError(ex, "An error occured creating topic {Topic}: {Reason}", _options.Topic, ex.Results[0].Error.Reason);
                throw;
            } catch (Exception ex) {
                _logger.LogError(ex, "An error occured creating topic {Topic}: {Reason}", _options.Topic, ex.Message);
                throw;
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
}