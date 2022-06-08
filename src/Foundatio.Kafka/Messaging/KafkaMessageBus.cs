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

public interface IKafkaMessageBus : IMessageBus {
    Task DeleteTopicAsync();
}

public class KafkaMessageBus : MessageBusBase<KafkaMessageBusOptions>, IKafkaMessageBus {
    private bool _isDisposed;
    private readonly CancellationTokenSource _messageBusDisposedCancellationTokenSource = new();
    private Task _listeningTask;
    private readonly AdminClientConfig _adminClientConfig;
    private readonly ConsumerConfig _consumerConfig;
    private readonly IProducer<string, byte[]> _producer;
    private readonly AsyncLock _lock = new();
    private bool _topicCreated;

    public KafkaMessageBus(KafkaMessageBusOptions options) : base(options) {
        _adminClientConfig = CreateAdminConfig();
        _consumerConfig = CreateConsumerConfig();
        var producerConfig = CreateProducerConfig();
        _producer = new ProducerBuilder<string, byte[]>(producerConfig)
            .SetLogHandler(LogHandler)
            .SetStatisticsHandler(LogStatisticsHandler)
            .SetErrorHandler(LogErrorHandler)
            .SetOAuthBearerTokenRefreshHandler(LogOAuthBearerTokenRefreshHandler)
            .Build();
    }

    public KafkaMessageBus(Builder<KafkaMessageBusOptionsBuilder, KafkaMessageBusOptions> config)
        : this(config(new KafkaMessageBusOptionsBuilder()).Build()) {
    }

    protected override Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("PublishImplAsync([{MessageType}])", messageType);

        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
            return AddDelayedMessageAsync(GetMappedMessageType(messageType), message, options.DeliveryDelay.Value);
        }

        var headers = new Headers {
            new Header(KafkaHeaders.MessageType, Encoding.UTF8.GetBytes(messageType)),
            new Header(KafkaHeaders.ContentType, Encoding.UTF8.GetBytes(_options.ContentType))
        };

        if (options?.CorrelationId is not null)
            headers.Add(new Header(KafkaHeaders.CorrelationId, Encoding.UTF8.GetBytes(options.CorrelationId)));

        var publishMessage = new Message<string, byte[]> {
            Key = _options.PublishKey,
            Value = SerializeMessageBody(messageType, message),
            Headers = headers
        };

        _producer.Produce(_options.Topic, publishMessage, deliveryReport => {
            if (!_logger.IsEnabled(LogLevel.Trace))
                return;

            if (deliveryReport.Error.Code != ErrorCode.NoError)
                _logger.LogWarning("Publish failure: {Reason}", deliveryReport.Error.Reason);
            else if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Published message to: {TopicPartitionOffset}", deliveryReport.TopicPartitionOffset);
        });

        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(IConsumer<string, byte[]> consumer, ConsumeResult<string, byte[]> consumeResult) {
        if (_subscribers.IsEmpty)
            return;

        using var _ = _logger.BeginScope(s => s
            .Property("GroupId", _consumerConfig.GroupId)
            .Property("Topic", consumeResult.Topic)
            .Property("Partition", consumeResult.Partition)
            .Property("Offset", consumeResult.Offset));

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("OnMessage(TopicPartitionOffset={TopicPartitionOffset} GroupId={GroupId})", consumeResult.TopicPartitionOffset, _consumerConfig.GroupId);

        try {
            string messageType = _options.ResolveMessageType?.Invoke(consumeResult);
            if (String.IsNullOrEmpty(messageType)) {
                var messageTypeHeader = consumeResult.Message.Headers.SingleOrDefault(x => x.Key.Equals(KafkaHeaders.MessageType));
                if (messageTypeHeader != null)
                    messageType = Encoding.UTF8.GetString(messageTypeHeader.GetValueBytes());
            }

            // What to do if message type is null?
            var message = ConvertToMessage(messageType, consumeResult.Message.Value);
            await SendMessageToSubscribersAsync(message).AnyContext();

            if (!_subscribers.IsEmpty)
                AcknowledgeMessage(consumer, consumeResult);
        } catch (Exception ex) {
            _logger.LogError(ex, "OnMessage(TopicPartitionOffset={TopicPartitionOffset} GroupId={GroupId}) Error deserializing message: {Message}", consumeResult.TopicPartitionOffset, _consumerConfig.GroupId, ex.Message);
        }
    }

    private void AcknowledgeMessage(IConsumer<string, byte[]> consumer, ConsumeResult<string, byte[]> consumeResult) {
        if (!_consumerConfig.EnableAutoCommit.GetValueOrDefault(true)) {
            _logger.LogTrace("Manual Commit: {TopicPartitionOffset}", consumeResult.TopicPartitionOffset);
            try {
                consumer.Commit(consumeResult);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error committing message {TopicPartitionOffset}: {Message}",
                    consumeResult.TopicPartitionOffset, ex.Message);
            }
        }

        if (!_consumerConfig.EnableAutoOffsetStore.GetValueOrDefault(true)) {
            _logger.LogTrace("Manual Store Offset: {TopicPartitionOffset}", consumeResult.TopicPartitionOffset);
            try {
                consumer.StoreOffset(consumeResult);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error storing message offset {TopicPartitionOffset}: {Message}",
                    consumeResult.TopicPartitionOffset, ex.Message);
            }
        }
    }

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("EnsureTopicSubscriptionAsync Topic={Topic}", _options.Topic);

        await EnsureTopicCreatedAsync().AnyContext();
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
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Already Listening: {Topic}", _options.Topic);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Start Listening: {Topic}", _options.Topic);

        _listeningTask = Task.Run(async () => {
            using var consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig)
                .SetLogHandler(LogHandler)
                .SetStatisticsHandler(LogStatisticsHandler)
                .SetErrorHandler(LogErrorHandler)
                .SetPartitionsAssignedHandler(LogPartitionAssignmentHandler)
                .SetPartitionsLostHandler(LogPartitionsLostHandler)
                .SetPartitionsRevokedHandler(LogPartitionsRevokedHandler)
                .SetOffsetsCommittedHandler(LogOffsetsCommittedHandler)
                .SetOAuthBearerTokenRefreshHandler(LogOAuthBearerTokenRefreshHandler)
                .Build();

            consumer.Subscribe(_options.Topic);
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Consumer {ConsumerName} subscribed to {Topic}", consumer.Name, _options.Topic);

            try {
                while (!_messageBusDisposedCancellationTokenSource.IsCancellationRequested) {
                    var consumeResult = consumer.Consume(_messageBusDisposedCancellationTokenSource.Token);
                    await OnMessageAsync(consumer, consumeResult).AnyContext();
                }
            } catch (OperationCanceledException) {
                // Don't log operation cancelled exceptions
            } catch (Exception ex) {
                _logger.LogError(ex, "Error consuming {Topic} message: {Message}", _options.Topic, ex.Message);
            } finally {
                consumer.Unsubscribe();
                consumer.Close();

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Consumer {ConsumerName} unsubscribed from {Topic}", consumer.Name, _options.Topic);
            }
        }, _messageBusDisposedCancellationTokenSource.Token);
    }

    public override void Dispose() {
        if (_isDisposed) {
            _logger.LogTrace("MessageBus {MessageBusId} dispose was already called", MessageBusId);
            return;
        }

        _isDisposed = true;
        _logger.LogTrace("MessageBus {MessageBusId} dispose", MessageBusId);

        int? queueSize = _producer?.Flush(TimeSpan.FromSeconds(15));
        if (queueSize > 0)
            _logger.LogTrace("Flushed producer {queueSize}", queueSize);

        _producer?.Dispose();
        _messageBusDisposedCancellationTokenSource.Cancel();
        _messageBusDisposedCancellationTokenSource.Dispose();
        base.Dispose();
    }

    private async Task EnsureTopicCreatedAsync() {
        if (_topicCreated || !_consumerConfig.AllowAutoCreateTopics.GetValueOrDefault())
            return;

        using var topicLock = await _lock.LockAsync().AnyContext();
        if (_topicCreated)
            return;

        using var adminClient = new AdminClientBuilder(_adminClientConfig)
            .SetLogHandler(LogHandler)
            .SetStatisticsHandler(LogStatisticsHandler)
            .SetErrorHandler(LogErrorHandler)
            .SetOAuthBearerTokenRefreshHandler(LogOAuthBearerTokenRefreshHandler)
            .Build();

        try {
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(2));
            bool isTopicExist = metadata.Topics.Any(t => t.Topic == _options.Topic);
            if (!isTopicExist) {
                if (_options.AllowAutoCreateTopics.GetValueOrDefault()) {
                    var topicSpecification = new TopicSpecification {
                        Name = _options.Topic,
                        Configs = _options.TopicConfigs,
                        ReplicasAssignments = _options.TopicReplicasAssignments
                    };

                    if (_options.TopicNumberOfPartitions.HasValue)
                        topicSpecification.NumPartitions = _options.TopicNumberOfPartitions.Value;
                    if (_options.TopicReplicationFactor.HasValue)
                        topicSpecification.ReplicationFactor = _options.TopicReplicationFactor.Value;

                    await adminClient.CreateTopicsAsync(new[] { topicSpecification }).AnyContext();
                    _topicCreated = true;

                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Created topic {Topic}", _options.Topic);
                } else {
                    throw new CreateTopicsException(new List<CreateTopicReport> {
                        new() { Error = new Error(ErrorCode.TopicException, "Topic doesn't exist"), Topic = _options.Topic }
                    });
                }
            }
        } catch (CreateTopicsException ex) when (ex.Results[0].Error.Code is ErrorCode.TopicAlreadyExists) {
            _topicCreated = true;
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Topic {Topic} already exists", _options.Topic);
        } catch (CreateTopicsException ex) {
            _logger.LogError(ex, "Error creating topic {Topic}: {Reason}", _options.Topic, ex.Results[0].Error.Reason);
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "Error creating topic {Topic}: {Reason}", _options.Topic, ex.Message);
            throw;
        }
    }

    async Task IKafkaMessageBus.DeleteTopicAsync() {
        if (!_topicCreated) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Skipping topic {Topic} deletion: topic wasn't created by this instance", _options.Topic);
            return;
        }

        using var topicLock = await _lock.LockAsync().AnyContext();
        if (!_topicCreated) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Skipping topic {Topic} deletion: topic wasn't created by this instance", _options.Topic);
            return;
        }

        using var adminClient = new AdminClientBuilder(_adminClientConfig)
            .SetLogHandler(LogHandler)
            .SetStatisticsHandler(LogStatisticsHandler)
            .SetErrorHandler(LogErrorHandler)
            .SetOAuthBearerTokenRefreshHandler(LogOAuthBearerTokenRefreshHandler)
            .Build();

        try {
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(2));
            bool isTopicExist = metadata.Topics.Any(t => t.Topic == _options.Topic);
            if (isTopicExist) {
                await adminClient.DeleteTopicsAsync(new[] { _options.Topic });
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Deleted topic {Topic}", _options.Topic);
            }

            _topicCreated = false;
        } catch (DeleteTopicsException ex) when (ex.Results[0].Error.Code is ErrorCode.TopicDeletionDisabled) {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Topic {Topic} deletion disabled", _options.Topic);
        } catch (DeleteTopicsException ex) {
            _logger.LogError(ex, "Error deleting topic {Topic}: {Reason}", _options.Topic, ex.Results[0].Error.Reason);
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "Error deleting topic {Topic}: {Reason}", _options.Topic, ex.Message);
            throw;
        }
    }

    private void LogHandler(IClient client, LogMessage message) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("[{LogLevel}] Client log: {Message}", message.Level, message.Message);
    }

    private void LogStatisticsHandler(IClient client, string statistics) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Client Statistics: {Statistics}", statistics);
    }

    private void LogPartitionAssignmentHandler(IConsumer<string, byte[]> consumer, List<TopicPartition> list) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Consumer partitions assigned: {@Partitions}", list);
    }

    private void LogPartitionsLostHandler(IConsumer<string, byte[]> consumer, List<TopicPartitionOffset> list) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Consumer partitions list: {@Partitions}", list);
    }

    private void LogPartitionsRevokedHandler(IConsumer<string, byte[]> consumer, List<TopicPartitionOffset> list) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Consumer partitions revoked: {@Partitions}", list);
    }

    private void LogOffsetsCommittedHandler(IConsumer<string, byte[]> consumer, CommittedOffsets offsets) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Consumer Committed Offsets: {@Offsets}", offsets.Offsets);
    }

    private void LogOAuthBearerTokenRefreshHandler(IClient client, string token) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Client refresh OAuth Bearer Token: {BearerToken}", token);
    }

    private void LogErrorHandler(IClient client, Error error) {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Client Error: [{ErrorCode}] {Message}", error.Code, error.Reason);
    }

    private ClientConfig CreateClientConfig() {
        var clientConfig = new ClientConfig();
        if (!String.IsNullOrEmpty(_options.ClientId)) clientConfig.ClientId = _options.ClientId;
        if (!String.IsNullOrEmpty(_options.BootstrapServers)) clientConfig.BootstrapServers = _options.BootstrapServers;
        if (!String.IsNullOrEmpty(_options.TopicBlacklist)) clientConfig.TopicBlacklist = _options.TopicBlacklist;
        if (!String.IsNullOrEmpty(_options.Debug)) clientConfig.Debug = _options.Debug;
        if (!String.IsNullOrEmpty(_options.SslCipherSuites)) clientConfig.SslCipherSuites = _options.SslCipherSuites;
        if (!String.IsNullOrEmpty(_options.SslCurvesList)) clientConfig.SslCurvesList = _options.SslCurvesList;
        if (!String.IsNullOrEmpty(_options.SslSigalgsList)) clientConfig.SslSigalgsList = _options.SslSigalgsList;
        if (!String.IsNullOrEmpty(_options.SslKeyLocation)) clientConfig.SslKeyLocation = _options.SslKeyLocation;
        if (!String.IsNullOrEmpty(_options.SslKeyPassword)) clientConfig.SslKeyPassword = _options.SslKeyPassword;
        if (!String.IsNullOrEmpty(_options.SslKeyPem)) clientConfig.SslKeyPem = _options.SslKeyPem;
        if (!String.IsNullOrEmpty(_options.SslCertificateLocation)) clientConfig.SslCertificateLocation = _options.SslCertificateLocation;
        if (!String.IsNullOrEmpty(_options.SslCertificatePem)) clientConfig.SslCertificatePem = _options.SslCertificatePem;
        if (!String.IsNullOrEmpty(_options.SslCaLocation)) clientConfig.SslCaLocation = _options.SslCaLocation;
        if (!String.IsNullOrEmpty(_options.SslCaPem)) clientConfig.SslCaPem = _options.SslCaPem;
        if (!String.IsNullOrEmpty(_options.SslCaCertificateStores)) clientConfig.SslCaCertificateStores = _options.SslCaCertificateStores;
        if (!String.IsNullOrEmpty(_options.SslCrlLocation)) clientConfig.SslCrlLocation = _options.SslCrlLocation;
        if (!String.IsNullOrEmpty(_options.SslKeystoreLocation)) clientConfig.SslKeystoreLocation = _options.SslKeystoreLocation;
        if (!String.IsNullOrEmpty(_options.SslKeystorePassword)) clientConfig.SslKeystorePassword = _options.SslKeystorePassword;
        if (!String.IsNullOrEmpty(_options.SslEngineLocation)) clientConfig.SslEngineLocation = _options.SslEngineLocation;
        if (!String.IsNullOrEmpty(_options.SslEngineId)) clientConfig.SslEngineId = _options.SslEngineId;
        if (!String.IsNullOrEmpty(_options.BrokerVersionFallback)) clientConfig.BrokerVersionFallback = _options.BrokerVersionFallback;
        if (!String.IsNullOrEmpty(_options.SaslKerberosServiceName)) clientConfig.SaslKerberosServiceName = _options.SaslKerberosServiceName;
        if (!String.IsNullOrEmpty(_options.SaslKerberosPrincipal)) clientConfig.SaslKerberosPrincipal = _options.SaslKerberosPrincipal;
        if (!String.IsNullOrEmpty(_options.SaslKerberosKinitCmd)) clientConfig.SaslKerberosKinitCmd = _options.SaslKerberosKinitCmd;
        if (!String.IsNullOrEmpty(_options.SaslKerberosKeytab)) clientConfig.SaslKerberosKeytab = _options.SaslKerberosKeytab;
        if (!String.IsNullOrEmpty(_options.SaslUsername)) clientConfig.SaslUsername = _options.SaslUsername;
        if (!String.IsNullOrEmpty(_options.SaslPassword)) clientConfig.SaslPassword = _options.SaslPassword;
        if (!String.IsNullOrEmpty(_options.SaslOauthbearerConfig)) clientConfig.SaslOauthbearerConfig = _options.SaslOauthbearerConfig;
        if (!String.IsNullOrEmpty(_options.PluginLibraryPaths)) clientConfig.PluginLibraryPaths = _options.PluginLibraryPaths;
        if (!String.IsNullOrEmpty(_options.ClientRack)) clientConfig.ClientRack = _options.ClientRack;
        if (_options.SaslMechanism.HasValue) clientConfig.SaslMechanism = _options.SaslMechanism;
        if (_options.Acks.HasValue) clientConfig.Acks = _options.Acks;
        if (_options.MessageMaxBytes.HasValue) clientConfig.MessageMaxBytes = _options.MessageMaxBytes;
        if (_options.MessageCopyMaxBytes.HasValue) clientConfig.MessageCopyMaxBytes = _options.MessageCopyMaxBytes;
        if (_options.ReceiveMessageMaxBytes.HasValue) clientConfig.ReceiveMessageMaxBytes = _options.ReceiveMessageMaxBytes;
        if (_options.MaxInFlight.HasValue) clientConfig.MaxInFlight = _options.MaxInFlight;
        if (_options.TopicMetadataRefreshIntervalMs.HasValue) clientConfig.TopicMetadataRefreshIntervalMs = _options.TopicMetadataRefreshIntervalMs;
        if (_options.MetadataMaxAgeMs.HasValue) clientConfig.MetadataMaxAgeMs = _options.MetadataMaxAgeMs;
        if (_options.TopicMetadataRefreshFastIntervalMs.HasValue) clientConfig.TopicMetadataRefreshFastIntervalMs = _options.TopicMetadataRefreshFastIntervalMs;
        if (_options.TopicMetadataRefreshSparse.HasValue) clientConfig.TopicMetadataRefreshSparse = _options.TopicMetadataRefreshSparse;
        if (_options.TopicMetadataPropagationMaxMs.HasValue) clientConfig.TopicMetadataPropagationMaxMs = _options.TopicMetadataPropagationMaxMs;
        if (_options.SocketTimeoutMs.HasValue) clientConfig.SocketTimeoutMs = _options.SocketTimeoutMs;
        if (_options.SocketSendBufferBytes.HasValue) clientConfig.SocketSendBufferBytes = _options.SocketSendBufferBytes;
        if (_options.SocketReceiveBufferBytes.HasValue) clientConfig.SocketReceiveBufferBytes = _options.SocketReceiveBufferBytes;
        if (_options.SocketKeepaliveEnable.HasValue) clientConfig.SocketKeepaliveEnable = _options.SocketKeepaliveEnable;
        if (_options.SocketNagleDisable.HasValue) clientConfig.SocketNagleDisable = _options.SocketNagleDisable;
        if (_options.SocketMaxFails.HasValue) clientConfig.SocketMaxFails = _options.SocketMaxFails;
        if (_options.BrokerAddressTtl.HasValue) clientConfig.BrokerAddressTtl = _options.BrokerAddressTtl;
        if (_options.BrokerAddressFamily.HasValue) clientConfig.BrokerAddressFamily = _options.BrokerAddressFamily;
        if (_options.ConnectionsMaxIdleMs.HasValue) clientConfig.ConnectionsMaxIdleMs = _options.ConnectionsMaxIdleMs;
        if (_options.ReconnectBackoffMs.HasValue) clientConfig.ReconnectBackoffMs = _options.ReconnectBackoffMs;
        if (_options.ReconnectBackoffMaxMs.HasValue) clientConfig.ReconnectBackoffMaxMs = _options.ReconnectBackoffMaxMs;
        if (_options.StatisticsIntervalMs.HasValue) clientConfig.StatisticsIntervalMs = _options.StatisticsIntervalMs;
        if (_options.LogQueue.HasValue) clientConfig.LogQueue = _options.LogQueue;
        if (_options.LogThreadName.HasValue) clientConfig.LogThreadName = _options.LogThreadName;
        if (_options.EnableRandomSeed.HasValue) clientConfig.EnableRandomSeed = _options.EnableRandomSeed;
        if (_options.LogConnectionClose.HasValue) clientConfig.LogConnectionClose = _options.LogConnectionClose;
        if (_options.InternalTerminationSignal.HasValue) clientConfig.InternalTerminationSignal = _options.InternalTerminationSignal;
        if (_options.ApiVersionRequest.HasValue) clientConfig.ApiVersionRequest = _options.ApiVersionRequest;
        if (_options.ApiVersionRequestTimeoutMs.HasValue) clientConfig.ApiVersionRequestTimeoutMs = _options.ApiVersionRequestTimeoutMs;
        if (_options.ApiVersionFallbackMs.HasValue) clientConfig.ApiVersionFallbackMs = _options.ApiVersionFallbackMs;
        if (_options.SecurityProtocol.HasValue) clientConfig.SecurityProtocol = _options.SecurityProtocol;
        if (_options.EnableSslCertificateVerification.HasValue) clientConfig.EnableSslCertificateVerification = _options.EnableSslCertificateVerification;
        if (_options.SslEndpointIdentificationAlgorithm.HasValue) clientConfig.SslEndpointIdentificationAlgorithm = _options.SslEndpointIdentificationAlgorithm;
        if (_options.SaslKerberosMinTimeBeforeRelogin.HasValue) clientConfig.SaslKerberosMinTimeBeforeRelogin = _options.SaslKerberosMinTimeBeforeRelogin;
        if (_options.EnableSaslOauthbearerUnsecureJwt.HasValue) clientConfig.EnableSaslOauthbearerUnsecureJwt = _options.EnableSaslOauthbearerUnsecureJwt;

        return clientConfig;
    }

    private AdminClientConfig CreateAdminConfig() {
        var clientConfig = CreateClientConfig();
        return new AdminClientConfig(clientConfig);
    }

    private ProducerConfig CreateProducerConfig() {
        var clientConfig = CreateClientConfig();
        var producerConfig = new ProducerConfig(clientConfig);

        // TODO: Producer config throws exception with this property
        if (!String.IsNullOrEmpty(_options.DeliveryReportFields)) producerConfig.DeliveryReportFields = _options.DeliveryReportFields;
        if (!String.IsNullOrEmpty(_options.TransactionalId)) producerConfig.TransactionalId = _options.TransactionalId;
        if (_options.EnableBackgroundPoll.HasValue) producerConfig.EnableBackgroundPoll = _options.EnableBackgroundPoll;
        if (_options.EnableDeliveryReports.HasValue) producerConfig.EnableDeliveryReports = _options.EnableDeliveryReports;
        if (_options.RequestTimeoutMs.HasValue) producerConfig.RequestTimeoutMs = _options.RequestTimeoutMs;
        if (_options.MessageTimeoutMs.HasValue) producerConfig.MessageTimeoutMs = _options.MessageTimeoutMs;
        if (_options.Partitioner.HasValue) producerConfig.Partitioner = _options.Partitioner;
        if (_options.CompressionLevel.HasValue) producerConfig.CompressionLevel = _options.CompressionLevel;
        if (_options.TransactionTimeoutMs.HasValue) producerConfig.TransactionTimeoutMs = _options.TransactionTimeoutMs;
        if (_options.EnableIdempotence.HasValue) producerConfig.EnableIdempotence = _options.EnableIdempotence;
        if (_options.EnableGaplessGuarantee.HasValue) producerConfig.EnableGaplessGuarantee = _options.EnableGaplessGuarantee;
        if (_options.QueueBufferingMaxMessages.HasValue) producerConfig.QueueBufferingMaxMessages = _options.QueueBufferingMaxMessages;
        if (_options.QueueBufferingMaxKbytes.HasValue) producerConfig.QueueBufferingMaxKbytes = _options.QueueBufferingMaxKbytes;
        if (_options.LingerMs.HasValue) producerConfig.LingerMs = _options.LingerMs;
        if (_options.MessageSendMaxRetries.HasValue) producerConfig.MessageSendMaxRetries = _options.MessageSendMaxRetries;
        if (_options.RetryBackoffMs.HasValue) producerConfig.RetryBackoffMs = _options.RetryBackoffMs;
        if (_options.QueueBufferingBackpressureThreshold.HasValue) producerConfig.QueueBufferingBackpressureThreshold = _options.QueueBufferingBackpressureThreshold;
        if (_options.CompressionType.HasValue) producerConfig.CompressionType = _options.CompressionType;
        if (_options.BatchNumMessages.HasValue) producerConfig.BatchNumMessages = _options.BatchNumMessages;
        if (_options.BatchSize.HasValue) producerConfig.BatchSize = _options.BatchSize;
        if (_options.StickyPartitioningLingerMs.HasValue) producerConfig.StickyPartitioningLingerMs = _options.StickyPartitioningLingerMs;

        return producerConfig;
    }

    private ConsumerConfig CreateConsumerConfig() {
        var clientConfig = CreateClientConfig();
        var consumerConfig = new ConsumerConfig(clientConfig);

        if (!String.IsNullOrEmpty(_options.GroupId)) consumerConfig.GroupId = _options.GroupId;
        if (!String.IsNullOrEmpty(_options.GroupInstanceId)) consumerConfig.GroupInstanceId = _options.GroupInstanceId;
        if (!String.IsNullOrEmpty(_options.ConsumeResultFields)) consumerConfig.ConsumeResultFields = _options.ConsumeResultFields;
        if (!String.IsNullOrEmpty(_options.GroupProtocolType)) consumerConfig.GroupProtocolType = _options.GroupProtocolType;
        if (_options.AutoOffsetReset.HasValue) consumerConfig.AutoOffsetReset = _options.AutoOffsetReset;
        if (_options.PartitionAssignmentStrategy.HasValue) consumerConfig.PartitionAssignmentStrategy = _options.PartitionAssignmentStrategy;
        if (_options.SessionTimeoutMs.HasValue) consumerConfig.SessionTimeoutMs = _options.SessionTimeoutMs;
        if (_options.HeartbeatIntervalMs.HasValue) consumerConfig.HeartbeatIntervalMs = _options.HeartbeatIntervalMs;
        if (_options.CoordinatorQueryIntervalMs.HasValue) consumerConfig.CoordinatorQueryIntervalMs = _options.CoordinatorQueryIntervalMs;
        if (_options.MaxPollIntervalMs.HasValue) consumerConfig.MaxPollIntervalMs = _options.MaxPollIntervalMs;
        if (_options.EnableAutoCommit.HasValue) consumerConfig.EnableAutoCommit = _options.EnableAutoCommit;
        if (_options.AutoCommitIntervalMs.HasValue) consumerConfig.AutoCommitIntervalMs = _options.AutoCommitIntervalMs;
        if (_options.EnableAutoOffsetStore.HasValue) consumerConfig.EnableAutoOffsetStore = _options.EnableAutoOffsetStore;
        if (_options.QueuedMinMessages.HasValue) consumerConfig.QueuedMinMessages = _options.QueuedMinMessages;
        if (_options.QueuedMaxMessagesKbytes.HasValue) consumerConfig.QueuedMaxMessagesKbytes = _options.QueuedMaxMessagesKbytes;
        if (_options.FetchWaitMaxMs.HasValue) consumerConfig.FetchWaitMaxMs = _options.FetchWaitMaxMs;
        if (_options.MaxPartitionFetchBytes.HasValue) consumerConfig.MaxPartitionFetchBytes = _options.MaxPartitionFetchBytes;
        if (_options.FetchMaxBytes.HasValue) consumerConfig.FetchMaxBytes = _options.FetchMaxBytes;
        if (_options.FetchMinBytes.HasValue) consumerConfig.FetchMinBytes = _options.FetchMinBytes;
        if (_options.FetchErrorBackoffMs.HasValue) consumerConfig.FetchErrorBackoffMs = _options.FetchErrorBackoffMs;
        if (_options.IsolationLevel.HasValue) consumerConfig.IsolationLevel = _options.IsolationLevel;
        if (_options.EnablePartitionEof.HasValue) consumerConfig.EnablePartitionEof = _options.EnablePartitionEof;
        if (_options.CheckCrcs.HasValue) consumerConfig.CheckCrcs = _options.CheckCrcs;
        if (_options.AllowAutoCreateTopics.HasValue) consumerConfig.AllowAutoCreateTopics = _options.AllowAutoCreateTopics;

        return consumerConfig;
    }

    public static class KafkaHeaders {
        public const string MessageType = "MessageType";
        public const string ContentType = "ContentType";
        public const string CorrelationId = "CorrelationId";
    }
}