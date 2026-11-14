using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Foundatio.Messaging.Transport;

/// <summary>Settings for <see cref="RabbitMQMessageTransport"/>.</summary>
public sealed record RabbitMQMessageTransportOptions
{
    /// <summary>
    /// The broker to connect to, as an <c>amqp://</c> or <c>amqps://</c> URI with credentials and virtual host, for
    /// example "amqp://guest:guest@localhost:5672". Required unless the transport is given a connection.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Hosts to fail over between, as "hostname" or "hostname:port" (brackets around an IPv6 address with a port). The
    /// host in <see cref="ConnectionString"/> is then ignored; its credentials, virtual host and TLS settings still apply.
    /// </summary>
    public IReadOnlyList<string>? Hosts { get; init; }

    /// <summary>
    /// Prepended to every exchange and queue name, so several environments or applications can share a virtual host.
    /// Letters, digits, '.', '_' and '-' only, and not starting with "amq".
    /// </summary>
    public string Prefix { get; init; } = "foundatio";

    /// <summary>
    /// When true (the default), delayed sends, delayed publishes and delayed retries go through delay queues the
    /// transport declares in the broker, so they survive restarts without any process running. When false the
    /// transport declares no delay support: Foundatio holds retries in-process and rejects delayed sends.
    /// </summary>
    public bool DelayedDelivery { get; init; } = true;

    /// <summary>
    /// The largest message body the broker accepts, its <c>max_message_size</c> (16 MiB by default on RabbitMQ 4).
    /// The transport reserves part of it for headers it moves into the body (see
    /// <see cref="RabbitMQMessageTransport.Capabilities"/>), so it must not be larger than the broker's setting.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// The RabbitMQ management API, for example "http://guest:guest@localhost:15672", used only by
    /// <see cref="RabbitMQMessageTransport.GetStatsAsync"/> to count delivered messages that are not settled yet.
    /// Credentials in the URI are used for basic authentication; without them, those of <see cref="ConnectionString"/>
    /// are. Without an endpoint, in-flight messages are not counted.
    /// </summary>
    public Uri? ManagementEndpoint { get; init; }

    /// <summary>
    /// Adjusts the connection factory the transport creates its connections with, after its defaults are applied (TLS,
    /// heartbeats, timeouts). Automatic recovery stays off: a receiver whose channel closes is replaced by Foundatio,
    /// and the transport opens a new connection when it next needs one.
    /// </summary>
    public Action<ConnectionFactory>? ConfigureConnectionFactory { get; init; }

    /// <summary>Used to turn delivery times into delays. Delay queues expire messages on the broker's clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Receives the transport's logs.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>How long after a failed abandon or dead-letter the delivery is released back to its queue.</summary>
    internal TimeSpan ReleaseDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times a receiver replaces a consumer whose prefetch is used up by unsettled deliveries before it waits for settlements.</summary>
    internal int MaxRetiredConsumers { get; init; } = 64;
}
