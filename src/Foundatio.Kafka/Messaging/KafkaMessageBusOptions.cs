using System;
using System.Collections.Generic;
using Confluent.Kafka;

namespace Foundatio.Messaging;

public class KafkaMessageBusOptions : SharedMessageBusOptions {
    /// <summary>
    /// bootstrap.servers
    ///     /// <summary>
    /// <value>
    /// The boot strap servers.
    /// </value>
    public string BootstrapServers { get; set; }

    /// <summary>
    /// group.id
    ///     /// <summary>
    /// <value>
    /// The group identifier.
    /// </value>
    public string GroupId { get; set; }

    /// <summary>
    /// Message Header Key of MessageType
    /// </summary>
    public string MessageType { get; set; } = "MessageType";

    /// <summary>
    /// Message Header Key of ContentType
    /// </summary>
    public string ContentType { get; set; } = "text/json";

    /// <summary>
    /// Publish message key
    /// </summary>
    public string PublishKey { get; set; }

    /// <summary>
    /// { "auto.commit.interval.ms", 5000 },
    ///     /// <summary>
    /// <value>
    /// The automatic commit interval ms.
    /// </value>
    public int AutoCommitIntervalMs { get; set; } = 5000;

    /// <summary>
    /// { "auto.offset.reset", "earliest" }
    ///     /// <summary>
    /// <value>
    /// The automatic off set reset.
    /// </value>
    public AutoOffsetReset AutoOffSetReset { get; set; }

    public IDictionary<string, object> Arguments { get; set; }

    /// <summary>
    ///  Path to client's public key (PEM) used for authentication. default: '' importance:low
    /// <summary>
    public string SslCertificateLocation { get; set; }

    /// <summary>
    ///SASL mechanism to use for authentication. Supported: GSSAPI, PLAIN, SCRAM-SHA-256,
    ///SCRAM-SHA-512. **NOTE**: Despite the name, you may not configure more than one
    ///mechanism.
    /// <summary>
    public SaslMechanism? SaslMechanism { get; set; }

    /// <summary>
    /// SASL username for use with the PLAIN and SASL-SCRAM-.. mechanisms default: importance: high
    /// <summary>
    public string SaslUsername { get; set; }

    /// <summary>
    /// SASL password for use with the PLAIN and SASL-SCRAM-
    /// <summary>
    public string SaslPassword { get; set; }

    /// <summary>
    /// File or directory path to CA certificate(s) for verifying the broker's key. Defaults:
    ///     On Windows the system's CA certificates are automatically looked up in the Windows
    ///     Root certificate store. On Mac OSX this configuration defaults to `probe`. It
    ///     is recommended to install openssl using Homebrew, to provide CA certificates.
    ///     On Linux install the distribution's ca-certificates package. If OpenSSL is statically
    ///     linked or `ssl.ca.location` is set to `probe` a list of standard paths will be
    ///     probed and the first one found will be used as the default CA certificate location
    ///     path. If OpenSSL is dynamically linked the OpenSSL library's default path will
    ///     be used (see `OPENSSLDIR` in `openssl version -a`). default: '' importance: low
    /// <summary>
    public string SslCaLocation { get; set; }

    /// <summary>
    /// Protocol used to communicate with brokers. default: plaintext importance: high
    /// <summary>
    public SecurityProtocol? SecurityProtocol { get; set; }

    /// <summary>
    /// The number of partitions for the new topic or -1 (the default) if a replica assignment
    ///     is specified.
    /// <summary>
    public int TopicNumberOfPartitions {
        get;
        set;
    } = -1;

    /// <summary>
    ///  The replication factor for the new topic or -1 (the default) if a replica assignment
    ///     is specified instead.
    /// <summary>
    public short TopicReplicationFactor {
        get;
        set;
    } = -1;

    /// <summary>
    /// Summary:
    ///     The configuration to use to create the new topic.
    /// </summary>
    public Dictionary<string, string> TopicConfigs {
        get;
        set;
    }

    /// <summary>
    /// Summary:
    ///     The name of the topic to be created (required).
    /// </summary>
    public string TopicName {
        get;
        set;
    }

    /// <summary>
    ///  Summary:
    ///     A map from partition id to replica ids (i.e., static broker ids) or null if the
    ///     number of partitions and replication factor are specified instead.
    /// </summary>
    public Dictionary<int, List<int>> TopicReplicasAssignments {
        get;
        set;
    }

    ///
    /// <summary>
    ///     This field indicates the number of acknowledgements the leader broker must receive
    ///     from ISR brokers before responding to the request: Zero=Broker does not send
    ///     any response/ack to client, One=The leader will write the record to its local
    ///     log but will respond without awaiting full acknowledgement from all followers.
    ///     All=Broker will block until message is committed by all in sync replicas (ISRs).
    ///     If there are less than min.insync.replicas (broker configuration) in the ISR
    ///     set the produce request will fail.
    /// <summary>
    public Acks? Acks { get; set; }

    ///
    /// <summary>
    ///     Client identifier. default: rdkafka importance: low
    /// <summary>
    public string ClientId { get; set; }

    ///
    /// <summary>
    ///     Maximum Kafka protocol request message size. Due to differing framing overhead
    ///     between protocol versions the producer is unable to reliably enforce a strict
    ///     max message limit at produce time and may exceed the maximum size by one message
    ///     in protocol ProduceRequests, the broker will enforce the the topic's `max.message.bytes`
    ///     limit (see Apache Kafka documentation). default: 1000000 importance: medium
    /// <summary>
    public int? MessageMaxBytes { get; set; }

    ///
    /// <summary>
    ///     Maximum size for message to be copied to buffer. Messages larger than this will
    ///     be passed by reference (zero-copy) at the expense of larger iovecs. default:
    ///     65535 importance: low
    /// <summary>
    public int? MessageCopyMaxBytes { get; set; }

    ///
    /// <summary>
    ///     Maximum Kafka protocol response message size. This serves as a safety precaution
    ///     to avoid memory exhaustion in case of protocol hickups. This value must be at
    ///     least `fetch.max.bytes` + 512 to allow for protocol overhead; the value is adjusted
    ///     automatically unless the configuration property is explicitly set. default: 100000000
    ///     importance: medium
    /// <summary>
    public int? ReceiveMessageMaxBytes { get; set; }

    ///
    /// <summary>
    ///     Maximum number of in-flight requests per broker connection. This is a generic
    ///     property applied to all broker communication, however it is primarily relevant
    ///     to produce requests. In particular, note that other mechanisms limit the number
    ///     of outstanding consumer fetch request per broker to one. default: 1000000 importance:
    ///     low
    /// <summary>
    public int? MaxInFlight { get; set; }

    ///
    /// <summary>
    ///     Period of time in milliseconds at which topic and broker metadata is refreshed
    ///     in order to proactively discover any new brokers, topics, partitions or partition
    ///     leader changes. Use -1 to disable the intervalled refresh (not recommended).
    ///     If there are no locally referenced topics (no topic objects created, no messages
    ///     produced, no subscription or no assignment) then only the broker list will be
    ///     refreshed every interval but no more often than every 10s. default: 300000 importance:
    ///     low
    /// <summary>
    public int? TopicMetadataRefreshIntervalMs { get; set; }

    ///
    /// <summary>
    ///     Metadata cache max age. Defaults to topic.metadata.refresh.interval.ms * 3 default:
    ///     900000 importance: low
    /// <summary>
    public int? MetadataMaxAgeMs { get; set; }

    ///
    /// <summary>
    ///     When a topic loses its leader a new metadata request will be enqueued with this
    ///     initial interval, exponentially increasing until the topic metadata has been
    ///     refreshed. This is used to recover quickly from transitioning leader brokers.
    ///     default: 250 importance: low
    /// <summary>
    public int? TopicMetadataRefreshFastIntervalMs { get; set; }

    ///
    /// <summary>
    ///     Sparse metadata requests (consumes less network bandwidth) default: true importance:
    ///     low
    /// <summary>
    public bool? TopicMetadataRefreshSparse { get; set; }

    ///
    /// <summary>
    ///     Apache Kafka topic creation is asynchronous and it takes some time for a new
    ///     topic to propagate throughout the cluster to all brokers. If a client requests
    ///     topic metadata after manual topic creation but before the topic has been fully
    ///     propagated to the broker the client is requesting metadata from, the topic will
    ///     seem to be non-existent and the client will mark the topic as such, failing queued
    ///     produced messages with `ERR__UNKNOWN_TOPIC`. This setting delays marking a topic
    ///     as non-existent until the configured propagation max time has passed. The maximum
    ///     propagation time is calculated from the time the topic is first referenced in
    ///     the client, e.g., on produce(). default: 30000 importance: low
    /// <summary>
    public int? TopicMetadataPropagationMaxMs { get; set; }

    ///
    /// <summary>
    ///     Topic blacklist, a comma-separated list of regular expressions for matching topic
    ///     names that should be ignored in broker metadata information as if the topics
    ///     did not exist. default: '' importance: low
    /// <summary>
    public string TopicBlacklist { get; set; }

    ///
    /// <summary>
    ///     A comma-separated list of debug contexts to enable. Detailed Producer debugging:
    ///     broker,topic,msg. Consumer: consumer,cgrp,topic,fetch default: '' importance:
    ///     medium
    /// <summary>
    public string Debug { get; set; }

    ///
    /// <summary>
    ///     Default timeout for network requests. Producer: ProduceRequests will use the
    ///     lesser value of `socket.timeout.ms` and remaining `message.timeout.ms` for the
    ///     first message in the batch. Consumer: FetchRequests will use `fetch.wait.max.ms`
    ///     + `socket.timeout.ms`. Admin: Admin requests will use `socket.timeout.ms` or
    ///     explicitly set `rd_kafka_AdminOptions_set_operation_timeout()` value. default:
    ///     60000 importance: low
    /// <summary>
    public int? SocketTimeoutMs { get; set; }

    ///
    /// <summary>
    ///     Broker socket send buffer size. System default is used if 0. default: 0 importance:
    ///     low 
    /// <summary>
    public int? SocketSendBufferBytes { get; set; }

    ///
    /// <summary>
    ///     Broker socket receive buffer size. System default is used if 0. default: 0 importance:
    ///     low
    /// <summary>
    public int? SocketReceiveBufferBytes { get; set; }

    ///
    /// <summary>
    ///     Enable TCP keep-alives (SO_KEEPALIVE) on broker sockets default: false importance:
    ///     low
    /// <summary>
    public bool? SocketKeepaliveEnable { get; set; }

    ///
    /// <summary>
    ///     Disable the Nagle algorithm (TCP_NODELAY) on broker sockets. default: false importance:
    ///     low
    /// <summary>
    public bool? SocketNagleDisable { get; set; }

    ///
    /// <summary>
    ///     Disconnect from broker when this number of send failures (e.g., timed out requests)
    ///     is reached. Disable with 0. WARNING: It is highly recommended to leave this setting
    ///     at its default value of 1 to avoid the client and broker to become desynchronized
    ///     in case of request timeouts. NOTE: The connection is automatically re-established.
    ///     default: 1 importance: low
    /// <summary>
    public int? SocketMaxFails { get; set; }

    ///
    /// <summary>
    ///     How long to cache the broker address resolving results (milliseconds). default:
    ///     1000 importance: low
    /// <summary>
    public int? BrokerAddressTtl { get; set; }

    ///
    /// <summary>
    ///     Allowed broker IP address families: any, v4, v6 default: any importance: low
    /// <summary>
    public BrokerAddressFamily? BrokerAddressFamily { get; set; }

    ///
    /// <summary>
    ///     Close broker connections after the specified time of inactivity. Disable with
    ///     0. If this property is left at its default value some heuristics are performed
    ///     to determine a suitable default value, this is currently limited to identifying
    ///     brokers on Azure (see librdkafka issue #3109 for more info). default: 0 importance:
    ///     medium
    /// <summary>
    public int? ConnectionsMaxIdleMs { get; set; }

    ///
    /// <summary>
    ///     The initial time to wait before reconnecting to a broker after the connection
    ///     has been closed. The time is increased exponentially until `reconnect.backoff.max.ms`
    ///     is reached. -25% to +50% jitter is applied to each reconnect backoff. A value
    ///     of 0 disables the backoff and reconnects immediately. default: 100 importance:
    ///     medium
    /// <summary>
    public int? ReconnectBackoffMs { get; set; }

    ///
    /// <summary>
    ///     The maximum time to wait before reconnecting to a broker after the connection
    ///     has been closed. default: 10000 importance: medium
    /// <summary>
    public int? ReconnectBackoffMaxMs { get; set; }

    ///
    /// <summary>
    ///     librdkafka statistics emit interval. The application also needs to register a
    ///     stats callback using `rd_kafka_conf_set_stats_cb()`. The granularity is 1000ms.
    ///     A value of 0 disables statistics. default: 0 importance: high
    /// <summary>
    public int? StatisticsIntervalMs { get; set; }

    ///
    /// <summary>
    ///     Disable spontaneous log_cb from internal librdkafka threads, instead enqueue
    ///     log messages on queue set with `rd_kafka_set_log_queue()` and serve log callbacks
    ///     or events through the standard poll APIs. **NOTE**: Log messages will linger
    ///     in a temporary queue until the log queue has been set. default: false importance:
    ///     low
    /// <summary>
    public bool? LogQueue { get; set; }

    ///
    /// <summary>
    ///     Print internal thread name in log messages (useful for debugging librdkafka internals)
    ///     default: true importance: low
    /// <summary>
    public bool? LogThreadName { get; set; }

    ///
    /// <summary>
    ///     If enabled librdkafka will initialize the PRNG with srand(current_time.milliseconds)
    ///     on the first invocation of rd_kafka_new() (required only if rand_r() is not available
    ///     on your platform). If disabled the application must call srand() prior to calling
    ///     rd_kafka_new(). default: true importance: low
    /// <summary>
    public bool? EnableRandomSeed { get; set; }

    ///
    /// <summary>
    ///     Log broker disconnects. It might be useful to turn this off when interacting
    ///     with 0.9 brokers with an aggressive `connection.max.idle.ms` value. default:
    ///     true importance: low
    /// <summary>
    public bool? LogConnectionClose { get; set; }

    ///
    /// <summary>
    ///     Signal that librdkafka will use to quickly terminate on rd_kafka_destroy(). If
    ///     this signal is not set then there will be a delay before rd_kafka_wait_destroyed()
    ///     returns true as internal threads are timing out their system calls. If this signal
    ///     is set however the delay will be minimal. The application should mask this signal
    ///     as an internal signal handler is installed. default: 0 importance: low
    ///     <summary>
    public int? InternalTerminationSignal { get; set; }

    ///
    /// <summary>
    ///     Request broker's supported API versions to adjust functionality to available
    ///     protocol features. If set to false, or the ApiVersionRequest fails, the fallback
    ///     version `broker.version.fallback` will be used. **NOTE**: Depends on broker version
    ///     >=0.10.0. If the request is not supported by (an older) broker the `broker.version.fallback`
    ///     fallback is used. default: true importance: high
    public bool? ApiVersionRequest { get; set; }

    ///
    /// <summary>
    ///     Timeout for broker API version requests. default: 10000 importance: low
    ///     <summary>
    public int? ApiVersionRequestTimeoutMs { get; set; }

    ///
    /// <summary>
    ///     Dictates how long the `broker.version.fallback` fallback is used in the case
    ///     the ApiVersionRequest fails. **NOTE**: The ApiVersionRequest is only issued when
    ///     a new connection to the broker is made (such as after an upgrade). default: 0
    ///     importance: medium
    ///     <summary>
    public int? ApiVersionFallbackMs { get; set; }

    ///
    /// <summary>
    ///     Older broker versions (before 0.10.0) provide no way for a client to query for
    ///     supported protocol features (ApiVersionRequest, see `api.version.request`) making
    ///     it impossible for the client to know what features it may use. As a workaround
    ///     a user may set this property to the expected broker version and the client will
    ///     automatically adjust its feature set accordingly if the ApiVersionRequest fails
    ///     (or is disabled). The fallback broker version will be used for `api.version.fallback.ms`.
    ///     Valid values are: 0.9.0, 0.8.2, 0.8.1, 0.8.0. Any other value >= 0.10, such as
    ///     0.10.2.1, enables ApiVersionRequests. default: 0.10.0 importance: medium
    ///     <summary>
    public string BrokerVersionFallback { get; set; }

    ///
    /// <summary>
    ///     A cipher suite is a named combination of authentication, encryption, MAC and
    ///     key exchange algorithm used to negotiate the security settings for a network
    ///     connection using TLS or SSL network protocol. See manual page for `ciphers(1)`
    ///     and `SSL_CTX_set_cipher_list(3). default: '' importance: low
    ///     <summary>
    public string SslCipherSuites { get; set; }

    ///
    /// <summary>
    ///     The supported-curves extension in the TLS ClientHello message specifies the curves
    ///     (standard/named, or 'explicit' GF(2^k) or GF(p)) the client is willing to have
    ///     the server use. See manual page for `SSL_CTX_set1_curves_list(3)`. OpenSSL >=
    ///     1.0.2 required. default: '' importance: low
    ///     <summary>
    public string SslCurvesList { get; set; }

    ///
    /// <summary>
    ///     The client uses the TLS ClientHello signature_algorithms extension to indicate
    ///     to the server which signature/hash algorithm pairs may be used in digital signatures.
    ///     See manual page for `SSL_CTX_set1_sigalgs_list(3)`. OpenSSL >= 1.0.2 required.
    ///     default: '' importance: low
    ///     <summary>
    public string SslSigalgsList { get; set; }

    ///
    /// <summary>
    ///     Path to client's private key (PEM) used for authentication. default: '' importance:
    ///     low
    ///     <summary>
    public string SslKeyLocation { get; set; }

    ///
    /// <summary>
    ///     Private key passphrase (for use with `ssl.key.location` and `set_ssl_cert()`)
    ///     default: '' importance: low
    ///     <summary>
    public string SslKeyPassword { get; set; }

    ///
    /// <summary>
    ///     Client's private key string (PEM format) used for authentication. default: ''
    ///     importance: low
    ///     <summary>
    public string SslKeyPem { get; set; }

    ///
    /// <summary>
    ///     Client's public key string (PEM format) used for authentication. default: ''
    ///     importance: low
    ///     <summary>
    public string SslCertificatePem { get; set; }

    ///
    /// <summary>
    ///     CA certificate string (PEM format) for verifying the broker's key. default: ''
    ///     importance: low
    ///     <summary>
    public string SslCaPem { get; set; }

    ///
    /// <summary>
    ///     Comma-separated list of Windows Certificate stores to load CA certificates from.
    ///     Certificates will be loaded in the same order as stores are specified. If no
    ///     certificates can be loaded from any of the specified stores an error is logged
    ///     and the OpenSSL library's default CA location is used instead. Store names are
    ///     typically one or more of: MY, Root, Trust, CA. default: Root importance: low
    ///     <summary>
    public string SslCaCertificateStores { get; set; }

    ///
    /// <summary>
    ///     Path to CRL for verifying broker's certificate validity. default: '' importance:
    ///     low
    ///     <summary>
    public string SslCrlLocation { get; set; }

    ///
    /// <summary>
    ///     Path to client's keystore (PKCS#12) used for authentication. default: '' importance:
    ///     low
    ///     <summary>
    public string SslKeystoreLocation { get; set; }

    ///
    /// <summary>
    ///     Client's keystore (PKCS#12) password. default: '' importance: low
    ///     <summary>
    public string SslKeystorePassword { get; set; }

    ///
    /// <summary>
    ///     Path to OpenSSL engine library. OpenSSL >= 1.1.0 required. default: '' importance:
    ///     low
    ///     <summary>
    public string SslEngineLocation { get; set; }

    ///
    /// <summary>
    ///     OpenSSL engine id is the name used for loading engine. default: dynamic importance:
    ///     low
    ///     <summary>
    public string SslEngineId { get; set; }

    ///
    /// <summary>
    ///     Enable OpenSSL's builtin broker (server) certificate verification. This verification
    ///     can be extended by the application by implementing a certificate_verify_cb. default:
    ///     true importance: low
    ///     <summary>
    public bool? EnableSslCertificateVerification { get; set; }

    ///
    /// <summary>
    ///     Endpoint identification algorithm to validate broker hostname using broker certificate.
    ///     https - Server (broker) hostname verification as specified in RFC2818. none -
    ///     No endpoint verification. OpenSSL >= 1.0.2 required. default: none importance:
    ///     low
    ///     <summary>
    public SslEndpointIdentificationAlgorithm? SslEndpointIdentificationAlgorithm { get; set; }

    ///
    /// <summary>
    ///     Kerberos principal name that Kafka runs as, not including /hostname@REALM default:
    ///     kafka importance: low
    ///     <summary>
    public string SaslKerberosServiceName { get; set; }

    ///
    /// <summary>
    ///     This client's Kerberos principal name. (Not supported on Windows, will use the
    ///     logon user's principal). default: kafkaclient importance: low
    ///     <summary>
    public string SaslKerberosPrincipal { get; set; }

    ///
    /// <summary>
    ///     Shell command to refresh or acquire the client's Kerberos ticket. This command
    ///     is executed on client creation and every sasl.kerberos.min.time.before.relogin
    ///     (0=disable). %{config.prop.name} is replaced by corresponding config object value.
    ///     default: kinit -R -t "%{sasl.kerberos.keytab}" -k %{sasl.kerberos.principal}
    ///     || kinit -t "%{sasl.kerberos.keytab}" -k %{sasl.kerberos.principal} importance:
    ///     low
    ///     <summary>
    public string SaslKerberosKinitCmd { get; set; }

    ///
    /// <summary>
    ///     Path to Kerberos keytab file. This configuration property is only used as a variable
    ///     in `sasl.kerberos.kinit.cmd` as ` ... -t "%{sasl.kerberos.keytab}"`. default:
    ///     '' importance: low
    ///     <summary>
    public string SaslKerberosKeytab { get; set; }

    ///
    /// <summary>
    ///     Minimum time in milliseconds between key refresh attempts. Disable automatic
    ///     key refresh by setting this property to 0. default: 60000 importance: low
    ///     <summary>
    public int? SaslKerberosMinTimeBeforeRelogin { get; set; }

    ///
    /// <summary>
    ///     SASL/OAUTHBEARER configuration. The format is implementation-dependent and must
    ///     be parsed accordingly. The default unsecured token implementation (see https:///tools.ietf.org/html/rfc7515#appendix-A.5)
    ///     recognizes space-separated name=value pairs with valid names including principalClaimName,
    ///     principal, scopeClaimName, scope, and lifeSeconds. The default value for principalClaimName
    ///     is "sub", the default value for scopeClaimName is "scope", and the default value
    ///     for lifeSeconds is 3600. The scope value is CSV format with the default value
    ///     being no/empty scope. For example: `principalClaimName=azp principal=admin scopeClaimName=roles
    ///     scope=role1,role2 lifeSeconds=600`. In addition, SASL extensions can be communicated
    ///     to the broker via `extension_NAME=value`. For example: `principal=admin extension_traceId=123`
    ///     default: '' importance: low
    ///     <summary>
    public string SaslOauthbearerConfig { get; set; }

    ///
    /// <summary>
    ///     Enable the builtin unsecure JWT OAUTHBEARER token handler if no oauthbearer_refresh_cb
    ///     has been set. This builtin handler should only be used for development or testing,
    ///     and not in production. default: false importance: low
    ///     <summary>
    public bool? EnableSaslOauthbearerUnsecureJwt { get; set; }

    ///
    /// <summary>
    ///     List of plugin libraries to load (; separated). The library search path is platform
    ///     dependent (see dlopen(3) for Unix and LoadLibrary() for Windows). If no filename
    ///     extension is specified the platform-specific extension (such as .dll or .so)
    ///     will be appended automatically. default: '' importance: low
    ///     <summary>
    public string PluginLibraryPaths { get; set; }

    ///
    /// <summary>
    ///     A rack identifier for this client. This can be any string value which indicates
    ///     where this client is physically located. It corresponds with the broker config
    ///     `broker.rack`. default: '' importance: low
    ///     <summary>
    public string ClientRack { get; set; }

    ///
    /// <summary>
    ///     Specifies whether or not the producer should start a background poll thread to
    ///     receive delivery reports and event notifications. Generally, this should be set
    ///     to true. If set to false, you will need to call the Poll function manually. default:
    ///     true importance: low
    ///     <summary>
    public bool? EnableBackgroundPoll { get; set; }

    ///
    /// <summary>
    ///     Specifies whether to enable notification of delivery reports. Typically you should
    ///     set this parameter to true. Set it to false for "fire and forget" semantics and
    ///     a small boost in performance. default: true importance: low
    ///     <summary>
    public bool? EnableDeliveryReports { get; set; }

    // Producer config throws exception with this property
    /////
    ///// <summary>
    /////     A comma separated list of fields that may be optionally set in delivery reports.
    /////     Disabling delivery report fields that you do not require will improve maximum
    /////     throughput and reduce memory usage. Allowed values: key, value, timestamp, headers,
    /////     status, all, none. default: all importance: low
    /////     <summary>
    //public string DeliveryReportFields { get; set; }

    ///
    /// <summary>
    ///     The ack timeout of the producer request in milliseconds. This value is only enforced
    ///     by the broker and relies on `request.required.acks` being != 0. default: 30000
    ///     importance: medium
    ///     <summary>
    public int? RequestTimeoutMs { get; set; }

    ///
    /// <summary>
    ///     Local message timeout. This value is only enforced locally and limits the time
    ///     a produced message waits for successful delivery. A time of 0 is infinite. This
    ///     is the maximum time librdkafka may use to deliver a message (including retries).
    ///     Delivery error occurs when either the retry count or the message timeout are
    ///     exceeded. The message timeout is automatically adjusted to `transaction.timeout.ms`
    ///     if `transactional.id` is configured. default: 300000 importance: high
    ///     <summary>
    public int? MessageTimeoutMs { get; set; }

    ///
    /// <summary>
    ///     Partitioner: `random` - random distribution, `consistent` - CRC32 hash of key
    ///     (Empty and NULL keys are mapped to single partition), `consistent_random` - CRC32
    ///     hash of key (Empty and NULL keys are randomly partitioned), `murmur2` - Java
    ///     Producer compatible Murmur2 hash of key (NULL keys are mapped to single partition),
    ///     `murmur2_random` - Java Producer compatible Murmur2 hash of key (NULL keys are
    ///     randomly partitioned. This is functionally equivalent to the default partitioner
    ///     in the Java Producer.), `fnv1a` - FNV-1a hash of key (NULL keys are mapped to
    ///     single partition), `fnv1a_random` - FNV-1a hash of key (NULL keys are randomly
    ///     partitioned). default: consistent_random importance: high
    ///     <summary>
    public Partitioner? Partitioner { get; set; }

    ///
    /// <summary>
    ///     Compression level parameter for algorithm selected by configuration property
    ///     `compression.codec`. Higher values will result in better compression at the cost
    ///     of more CPU usage. Usable range is algorithm-dependent: [0-9] for gzip; [0-12]
    ///     for lz4; only 0 for snappy; -1 = codec-dependent default compression level. default:
    ///     -1 importance: medium
    ///     <summary>
    public int? CompressionLevel { get; set; }

    ///
    /// <summary>
    ///     Enables the transactional producer. The transactional.id is used to identify
    ///     the same transactional producer instance across process restarts. It allows the
    ///     producer to guarantee that transactions corresponding to earlier instances of
    ///     the same producer have been finalized prior to starting any new transactions,
    ///     and that any zombie instances are fenced off. If no transactional.id is provided,
    ///     then the producer is limited to idempotent delivery (if enable.idempotence is
    ///     set). Requires broker version >= 0.11.0. default: '' importance: high
    ///     <summary>
    public string TransactionalId { get; set; }

    ///
    /// <summary>
    ///     The maximum amount of time in milliseconds that the transaction coordinator will
    ///     wait for a transaction status update from the producer before proactively aborting
    ///     the ongoing transaction. If this value is larger than the `transaction.max.timeout.ms`
    ///     setting in the broker, the init_transactions() call will fail with ERR_INVALID_TRANSACTION_TIMEOUT.
    ///     The transaction timeout automatically adjusts `message.timeout.ms` and `socket.timeout.ms`,
    ///     unless explicitly configured in which case they must not exceed the transaction
    ///     timeout (`socket.timeout.ms` must be at least 100ms lower than `transaction.timeout.ms`).
    ///     This is also the default timeout value if no timeout (-1) is supplied to the
    ///     transactional API methods. default: 60000 importance: medium
    ///     <summary>
    public int? TransactionTimeoutMs { get; set; }

    ///
    /// <summary>
    ///     When set to `true`, the producer will ensure that messages are successfully produced
    ///     exactly once and in the original produce order. The following configuration properties
    ///     are adjusted automatically (if not modified by the user) when idempotence is
    ///     enabled: `max.in.flight.requests.per.connection=5` (must be less than or equal
    ///     to 5), `retries=INT32_MAX` (must be greater than 0), `acks=all`, `queuing.strategy=fifo`.
    ///     Producer instantation will fail if user-supplied configuration is incompatible.
    ///     default: false importance: high
    ///     <summary>
    public bool? EnableIdempotence { get; set; }

    ///
    /// <summary>
    ///     **EXPERIMENTAL**: subject to change or removal. When set to `true`, any error
    ///     that could result in a gap in the produced message series when a batch of messages
    ///     fails, will raise a fatal error (ERR__GAPLESS_GUARANTEE) and stop the producer.
    ///     Messages failing due to `message.timeout.ms` are not covered by this guarantee.
    ///     Requires `enable.idempotence=true`. default: false importance: low
    ///     <summary>
    public bool? EnableGaplessGuarantee { get; set; }

    ///
    /// <summary>
    ///     Maximum number of messages allowed on the producer queue. This queue is shared
    ///     by all topics and partitions. default: 100000 importance: high
    ///     <summary>
    public int? QueueBufferingMaxMessages { get; set; }

    ///
    /// <summary>
    ///     Maximum total message size sum allowed on the producer queue. This queue is shared
    ///     by all topics and partitions. This property has higher priority than queue.buffering.max.messages.
    ///     default: 1048576 importance: high
    ///     <summary>
    public int? QueueBufferingMaxKbytes { get; set; }

    ///
    /// <summary>
    ///     Delay in milliseconds to wait for messages in the producer queue to accumulate
    ///     before constructing message batches (MessageSets) to transmit to brokers. A higher
    ///     value allows larger and more effective (less overhead, improved compression)
    ///     batches of messages to accumulate at the expense of increased message delivery
    ///     latency. default: 5 importance: high
    ///     <summary>
    public double? LingerMs { get; set; }

    ///
    /// <summary>
    ///     How many times to retry sending a failing Message. **Note:** retrying may cause
    ///     reordering unless `enable.idempotence` is set to true. default: 2147483647 importance:
    ///     high
    ///     <summary>
    public int? MessageSendMaxRetries { get; set; }

    ///
    /// <summary>
    ///     The backoff time in milliseconds before retrying a protocol request. default:
    ///     100 importance: medium
    ///     <summary>
    public int? RetryBackoffMs { get; set; }

    ///
    /// <summary>
    ///     The threshold of outstanding not yet transmitted broker requests needed to backpressure
    ///     the producer's message accumulator. If the number of not yet transmitted requests
    ///     equals or exceeds this number, produce request creation that would have otherwise
    ///     been triggered (for example, in accordance with linger.ms) will be delayed. A
    ///     lower number yields larger and more effective batches. A higher value can improve
    ///     latency when using compression on slow machines. default: 1 importance: low
    ///     <summary>
    public int? QueueBufferingBackpressureThreshold { get; set; }

    ///
    /// <summary>
    ///     compression codec to use for compressing message sets. This is the default value
    ///     for all topics, may be overridden by the topic configuration property `compression.codec`.
    ///     default: none importance: medium
    ///     <summary>
    public CompressionType? CompressionType { get; set; }

    ///
    /// <summary>
    ///     Maximum number of messages batched in one MessageSet. The total MessageSet size
    ///     is also limited by batch.size and message.max.bytes. default: 10000 importance:
    ///     medium
    ///     <summary>
    public int? BatchNumMessages { get; set; }

    ///
    /// <summary>
    ///     Maximum size (in bytes) of all messages batched in one MessageSet, including
    ///     protocol framing overhead. This limit is applied after the first message has
    ///     been added to the batch, regardless of the first message's size, this is to ensure
    ///     that messages that exceed batch.size are produced. The total MessageSet size
    ///     is also limited by batch.num.messages and message.max.bytes. default: 1000000
    ///     importance: medium
    ///     <summary>
    public int? BatchSize { get; set; }

    ///
    /// <summary>
    ///     Delay in milliseconds to wait to assign new sticky partitions for each topic.
    ///     By default, set to double the time of linger.ms. To disable sticky behavior,
    ///     set to 0. This behavior affects messages with the key NULL in all cases, and
    ///     messages with key lengths of zero when the consistent_random partitioner is in
    ///     use. These messages would otherwise be assigned randomly. A higher value allows
    ///     for more effective batching of these messages. default: 10 importance: low
    ///     <summary>
    public int? StickyPartitioningLingerMs { get; set; }

    ///
    /// <summary>
    ///     A comma separated list of fields that may be optionally set in Confluent.Kafka.ConsumeResult`2
    ///     objects returned by the Confluent.Kafka.Consumer`2.Consume(System.TimeSpan) method.
    ///     Disabling fields that you do not require will improve throughput and reduce memory
    ///     consumption. Allowed values: headers, timestamp, topic, all, none default: all
    ///     importance: low
    ///      <summary>
    public string ConsumeResultFields { get; set; }

    ///
    /// <summary>
    ///     Action to take when there is no initial offset in offset store or the desired
    ///     offset is out of range: 'smallest','earliest' - automatically reset the offset
    ///     to the smallest offset, 'largest','latest' - automatically reset the offset to
    ///     the largest offset, 'error' - trigger an error (ERR__AUTO_OFFSET_RESET) which
    ///     is retrieved by consuming messages and checking 'message->err'. default: largest
    ///     importance: high
    ///      <summary>
    public AutoOffsetReset? AutoOffsetReset { get; set; }

    ///
    /// <summary>
    ///     Enable static group membership. Static group members are able to leave and rejoin
    ///     a group within the configured `session.timeout.ms` without prompting a group
    ///     rebalance. This should be used in combination with a larger `session.timeout.ms`
    ///     to avoid group rebalances caused by transient unavailability (e.g. process restarts).
    ///     Requires broker version >= 2.3.0. default: '' importance: medium
    ///      <summary>
    public string GroupInstanceId { get; set; }

    ///
    /// <summary>
    ///     The name of one or more partition assignment strategies. The elected group leader
    ///     will use a strategy supported by all members of the group to assign partitions
    ///     to group members. If there is more than one eligible strategy, preference is
    ///     determined by the order of this list (strategies earlier in the list have higher
    ///     priority). Cooperative and non-cooperative (eager) strategies must not be mixed.
    ///     Available strategies: range, roundrobin, cooperative-sticky. default: range,roundrobin
    ///     importance: medium
    ///     <summary>
    public PartitionAssignmentStrategy? PartitionAssignmentStrategy { get; set; }
    ///
    /// <summary>
    ///     Client group session and failure detection timeout. The consumer sends periodic
    ///     heartbeats (heartbeat.interval.ms) to indicate its liveness to the broker. If
    ///     no hearts are received by the broker for a group member within the session timeout,
    ///     the broker will remove the consumer from the group and trigger a rebalance. The
    ///     allowed range is configured with the **broker** configuration properties `group.min.session.timeout.ms`
    ///     and `group.max.session.timeout.ms`. Also see `max.poll.interval.ms`. default:
    ///     45000 importance: high
    ///     <summary>
    public int? SessionTimeoutMs { get; set; }

    ///
    /// <summary>
    ///     Group session keepalive heartbeat interval. default: 3000 importance: low
    ///     <summary>
    public int? HeartbeatIntervalMs { get; set; }

    ///
    /// <summary>
    ///     Group protocol type. NOTE: Currently, the only supported group protocol type
    ///     is `consumer`. default: consumer importance: low
    ///     <summary>
    public string GroupProtocolType { get; set; }

    ///
    /// <summary>
    ///     How often to query for the current client group coordinator. If the currently
    ///     assigned coordinator is down the configured query interval will be divided by
    ///     ten to more quickly recover in case of coordinator reassignment. default: 600000
    ///     importance: low
    ///     <summary>
    public int? CoordinatorQueryIntervalMs { get; set; }

    ///
    /// <summary>
    ///     Maximum allowed time between calls to consume messages (e.g., rd_kafka_consumer_poll())
    ///     for high-level consumers. If this interval is exceeded the consumer is considered
    ///     failed and the group will rebalance in order to reassign the partitions to another
    ///     consumer group member. Warning: Offset commits may be not possible at this point.
    ///     Note: It is recommended to set `enable.auto.offset.store=false` for long-time
    ///     processing applications and then explicitly store offsets (using offsets_store())
    ///     *after* message processing, to make sure offsets are not auto-committed prior
    ///     to processing has finished. The interval is checked two times per second. See
    ///     KIP-62 for more information. default: 300000 importance: high
    ///     <summary>
    public int? MaxPollIntervalMs { get; set; }

    ///
    /// <summary>
    ///     Automatically and periodically commit offsets in the background. Note: setting
    ///     this to false does not prevent the consumer from fetching previously committed
    ///     start offsets. To circumvent this behaviour set specific start offsets per partition
    ///     in the call to assign(). default: true importance: high
    ///     <summary>
    public bool? EnableAutoCommit { get; set; }

    ///
    /// <summary>
    ///     Automatically store offset of last message provided to application. The offset
    ///     store is an in-memory store of the next offset to (auto-)commit for each partition.
    ///     default: true importance: high
    ///     <summary>
    public bool? EnableAutoOffsetStore { get; set; }

    ///
    /// <summary>
    ///     Minimum number of messages per topic+partition librdkafka tries to maintain in
    ///     the local consumer queue. default: 100000 importance: medium
    ///     <summary>
    public int? QueuedMinMessages { get; set; }

    ///
    /// <summary>
    ///     Maximum number of kilobytes of queued pre-fetched messages in the local consumer
    ///     queue. If using the high-level consumer this setting applies to the single consumer
    ///     queue, regardless of the number of partitions. When using the legacy simple consumer
    ///     or when separate partition queues are used this setting applies per partition.
    ///     This value may be overshot by fetch.message.max.bytes. This property has higher
    ///     priority than queued.min.messages. default: 65536 importance: medium
    ///     <summary>
    public int? QueuedMaxMessagesKbytes { get; set; }

    ///
    /// <summary>
    ///     Maximum time the broker may wait to fill the Fetch response with fetch.min.bytes
    ///     of messages. default: 500 importance: low
    ///     <summary>
    public int? FetchWaitMaxMs { get; set; }

    ///
    /// <summary>
    ///     Initial maximum number of bytes per topic+partition to request when fetching
    ///     messages from the broker. If the client encounters a message larger than this
    ///     value it will gradually try to increase it until the entire message can be fetched.
    ///     default: 1048576 importance: medium
    ///     <summary>
    public int? MaxPartitionFetchBytes { get; set; }

    ///
    /// <summary>
    ///     Maximum amount of data the broker shall return for a Fetch request. Messages
    ///     are fetched in batches by the consumer and if the first message batch in the
    ///     first non-empty partition of the Fetch request is larger than this value, then
    ///     the message batch will still be returned to ensure the consumer can make progress.
    ///     The maximum message batch size accepted by the broker is defined via `message.max.bytes`
    ///     (broker config) or `max.message.bytes` (broker topic config). `fetch.max.bytes`
    ///     is automatically adjusted upwards to be at least `message.max.bytes` (consumer
    ///     config). default: 52428800 importance: medium
    ///     <summary>
    public int? FetchMaxBytes { get; set; }

    ///
    /// <summary>
    ///     Minimum number of bytes the broker responds with. If fetch.wait.max.ms expires
    ///     the accumulated data will be sent to the client regardless of this setting. default:
    ///     1 importance: low
    ///     <summary>
    public int? FetchMinBytes { get; set; }

    ///
    /// <summary>
    ///     How long to postpone the next fetch request for a topic+partition in case of
    ///     a fetch error. default: 500 importance: medium
    ///     <summary>
    public int? FetchErrorBackoffMs { get; set; }

    ///
    /// <summary>
    ///     Controls how to read messages written transactionally: `read_committed` - only
    ///     return transactional messages which have been committed. `read_uncommitted` -
    ///     return all messages, even transactional messages which have been aborted. default:
    ///     read_committed importance: high
    ///     <summary>
    public IsolationLevel? IsolationLevel { get; set; }

    ///
    /// <summary>
    ///     Emit RD_KAFKA_RESP_ERR__PARTITION_EOF event whenever the consumer reaches the
    ///     end of a partition. default: false importance: low
    ///     <summary>
    public bool? EnablePartitionEof { get; set; }

    ///
    /// <summary>
    ///     Verify CRC32 of consumed messages, ensuring no on-the-wire or on-disk corruption
    ///     to the messages occurred. This check comes at slightly increased CPU usage. default:
    ///     false importance: medium
    ///     <summary>
    public bool? CheckCrcs { get; set; }

    ///
    /// <summary>
    ///     Allow automatic topic creation on the broker when subscribing to or assigning
    ///     non-existent topics. The broker must also be configured with `auto.create.topics.enable=true`
    ///     for this configuraiton to take effect. Note: The default value (false) is different
    ///     from the Java consumer (true). Requires broker version >= 0.11.0.0, for older
    ///     broker versions only the broker configuration applies. default: false importance:
    ///     low
    ///     <summary>
    public bool? AllowAutoCreateTopics { get; set; }
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

    public KafkaMessageBusOptionsBuilder AutoOffSetReset(Confluent.Kafka.AutoOffsetReset? autoOffSetReset) {
        Target.AutoOffSetReset = autoOffSetReset ?? throw new ArgumentNullException(nameof(autoOffSetReset));
        return this;
    }

    public KafkaMessageBusOptionsBuilder Arguments(IDictionary<string, object> arguments) {
        Target.Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
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

    public KafkaMessageBusOptionsBuilder ClientId(string? clientId) {
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

    public KafkaMessageBusOptionsBuilder TopicName(string topicName) {
        Target.TopicName = topicName ?? throw new ArgumentNullException(nameof(topicName));
        return this;
    }

    public KafkaMessageBusOptionsBuilder Debug(string? debug) {
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
    // Producer config throws exception with this property
    //public KafkaMessageBusOptionsBuilder DeliveryReportFields(string deliveryReportFields) {
    //    Target.DeliveryReportFields = deliveryReportFields ?? throw new ArgumentNullException(nameof(deliveryReportFields));
    //    return this;
    //}
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

