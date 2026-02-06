using System;
using Amazon;
using Amazon.Runtime;

namespace Foundatio.Messaging;

public class SQSMessageBusOptions : SharedMessageBusOptions
{
    /// <summary>
    /// The connection string containing AWS credentials and configuration.
    /// Format: "AccessKey=xxx;SecretKey=xxx;Region=us-east-1" or "ServiceUrl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx"
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// The AWS credentials to use for authentication. If not specified, uses default credential resolution.
    /// </summary>
    /// <seealso cref="Amazon.Runtime.AWSCredentials"/>
    public AWSCredentials Credentials { get; set; }

    /// <summary>
    /// The AWS region endpoint. If not specified, uses default region resolution.
    /// </summary>
    /// <seealso cref="Amazon.RegionEndpoint"/>
    public RegionEndpoint Region { get; set; }

    /// <summary>
    /// The service URL for LocalStack or custom endpoints. When set, overrides the region endpoint.
    /// </summary>
    public string ServiceUrl { get; set; }

    /// <summary>
    /// Whether the SNS topic can be created if it doesn't exist. Default is true.
    /// </summary>
    public bool CanCreateTopic { get; set; } = true;

    /// <summary>
    /// The name of the SQS subscription queue. If not specified, a unique queue name will be generated.
    /// Use this for durable subscriptions that persist across restarts.
    /// </summary>
    public string SubscriptionQueueName { get; set; }

    /// <summary>
    /// Whether the subscription queue should be automatically deleted when the message bus is disposed.
    /// Default is true for ephemeral queues, set to false for durable subscriptions.
    /// </summary>
    public bool SubscriptionQueueAutoDelete { get; set; } = true;

    /// <summary>
    /// The timeout for reading messages from the SQS queue using long polling. Default is 20 seconds.
    /// </summary>
    public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The interval between dequeue attempts when no messages are available. Default is 1 second.
    /// </summary>
    public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The visibility timeout for messages. Messages will be invisible to other consumers
    /// for this duration after being received. If not set, uses SQS default (30 seconds).
    /// </summary>
    public TimeSpan? MessageVisibilityTimeout { get; set; }

    /// <summary>
    /// Whether to use SQS managed server-side encryption (SSE-SQS). Default is false.
    /// </summary>
    public bool SqsManagedSseEnabled { get; set; }

    /// <summary>
    /// The KMS master key ID for server-side encryption (SSE-KMS).
    /// </summary>
    public string KmsMasterKeyId { get; set; }

    /// <summary>
    /// The KMS data key reuse period in seconds. Default is 300 seconds (5 minutes).
    /// </summary>
    public int KmsDataKeyReusePeriodSeconds { get; set; } = 300;

    /// <summary>
    /// Optional function to resolve the SNS topic name for a given message type.
    /// If not set or returns null, falls back to the default <see cref="SharedMessageBusOptions.Topic"/>.
    /// This enables routing different message types to different SNS topics.
    /// </summary>
    public Func<Type, string> TopicResolver { get; set; }
}

public class SQSMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<SQSMessageBusOptions, SQSMessageBusOptionsBuilder>
{
    /// <summary>
    /// Sets the connection string containing AWS credentials and configuration.
    /// </summary>
    public SQSMessageBusOptionsBuilder ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        Target.ConnectionString = connectionString;
        return this;
    }

    /// <summary>
    /// Sets the AWS credentials to use for authentication.
    /// </summary>
    public SQSMessageBusOptionsBuilder Credentials(AWSCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Target.Credentials = credentials;
        return this;
    }

    /// <summary>
    /// Sets the AWS credentials using access key and secret key.
    /// </summary>
    public SQSMessageBusOptionsBuilder Credentials(string accessKey, string secretKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessKey);
        ArgumentException.ThrowIfNullOrEmpty(secretKey);

        Target.Credentials = new BasicAWSCredentials(accessKey, secretKey);
        return this;
    }

    /// <summary>
    /// Sets the AWS region endpoint.
    /// </summary>
    public SQSMessageBusOptionsBuilder Region(RegionEndpoint region)
    {
        ArgumentNullException.ThrowIfNull(region);
        Target.Region = region;
        return this;
    }

    /// <summary>
    /// Sets the AWS region by system name (e.g., "us-east-1").
    /// </summary>
    public SQSMessageBusOptionsBuilder Region(string region)
    {
        ArgumentException.ThrowIfNullOrEmpty(region);
        Target.Region = RegionEndpoint.GetBySystemName(region);
        return this;
    }

    /// <summary>
    /// Sets the service URL for LocalStack or custom endpoints.
    /// </summary>
    public SQSMessageBusOptionsBuilder ServiceUrl(string serviceUrl)
    {
        Target.ServiceUrl = serviceUrl;
        return this;
    }

    /// <summary>
    /// Sets whether the SNS topic can be created if it doesn't exist.
    /// </summary>
    public SQSMessageBusOptionsBuilder CanCreateTopic(bool enabled)
    {
        Target.CanCreateTopic = enabled;
        return this;
    }

    /// <summary>
    /// Enables automatic topic creation if it doesn't exist.
    /// </summary>
    public SQSMessageBusOptionsBuilder EnableCreateTopic() => CanCreateTopic(true);

    /// <summary>
    /// Disables automatic topic creation. An exception will be thrown if the topic doesn't exist.
    /// </summary>
    public SQSMessageBusOptionsBuilder DisableCreateTopic() => CanCreateTopic(false);

    /// <summary>
    /// Sets the subscription queue name for durable subscriptions.
    /// </summary>
    public SQSMessageBusOptionsBuilder SubscriptionQueueName(string queueName)
    {
        Target.SubscriptionQueueName = queueName;
        return this;
    }

    /// <summary>
    /// Configures whether the subscription queue should be auto-deleted on dispose.
    /// </summary>
    public SQSMessageBusOptionsBuilder SubscriptionQueueAutoDelete(bool autoDelete)
    {
        Target.SubscriptionQueueAutoDelete = autoDelete;
        return this;
    }

    /// <summary>
    /// Configures a durable subscription that persists across restarts.
    /// Sets the queue name and disables auto-delete.
    /// </summary>
    public SQSMessageBusOptionsBuilder UseDurableSubscription(string queueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);

        Target.SubscriptionQueueName = queueName;
        Target.SubscriptionQueueAutoDelete = false;
        return this;
    }

    /// <summary>
    /// Sets the timeout for reading messages from the SQS queue using long polling.
    /// </summary>
    public SQSMessageBusOptionsBuilder ReadQueueTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        Target.ReadQueueTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the interval between dequeue attempts when no messages are available.
    /// </summary>
    public SQSMessageBusOptionsBuilder DequeueInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);

        Target.DequeueInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the visibility timeout for messages.
    /// </summary>
    public SQSMessageBusOptionsBuilder MessageVisibilityTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        Target.MessageVisibilityTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Enables KMS server-side encryption (SSE-KMS) with the specified master key.
    /// </summary>
    public SQSMessageBusOptionsBuilder UseKmsEncryption(string kmsMasterKeyId, int kmsKeyReusePeriodSeconds = 300)
    {
        ArgumentException.ThrowIfNullOrEmpty(kmsMasterKeyId);
        Target.KmsMasterKeyId = kmsMasterKeyId;
        Target.KmsDataKeyReusePeriodSeconds = kmsKeyReusePeriodSeconds;
        Target.SqsManagedSseEnabled = false;
        return this;
    }

    /// <summary>
    /// Enables SQS managed server-side encryption (SSE-SQS).
    /// </summary>
    public SQSMessageBusOptionsBuilder UseSqsManagedEncryption()
    {
        Target.SqsManagedSseEnabled = true;
        Target.KmsMasterKeyId = null;
        return this;
    }

    /// <summary>
    /// Sets a function to resolve the SNS topic name for a given message type.
    /// This enables routing different message types to different SNS topics.
    /// </summary>
    /// <param name="resolver">
    /// A function that takes a message type and returns the topic name.
    /// Return null to use the default topic.
    /// </param>
    public SQSMessageBusOptionsBuilder TopicResolver(Func<Type, string> resolver)
    {
        Target.TopicResolver = resolver;
        return this;
    }

    /// <inheritdoc />
    public override SQSMessageBusOptions Build()
    {
        if (String.IsNullOrEmpty(Target.ConnectionString))
            return Target;

        var connectionString = new SQSMessageBusConnectionStringBuilder(Target.ConnectionString);
        Target.Credentials ??= connectionString.GetCredentials();
        Target.Region ??= connectionString.GetRegion();

        if (String.IsNullOrEmpty(Target.ServiceUrl) && !String.IsNullOrEmpty(connectionString.ServiceUrl))
            Target.ServiceUrl = connectionString.ServiceUrl;

        return Target;
    }
}
