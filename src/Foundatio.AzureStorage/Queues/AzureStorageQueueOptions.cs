using System;
using Azure.Core;

namespace Foundatio.Queues;

/// <summary>
/// Compatibility mode for Azure Storage Queue message format.
/// </summary>
public enum AzureStorageQueueCompatibilityMode
{
    /// <summary>
    /// Default mode: Uses message envelope for full metadata support.
    /// Supports CorrelationId and Properties. New installations should use this mode.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Legacy mode: Raw message body with Base64 encoding for v11 SDK compatibility.
    /// Use this for backward compatibility with existing queues that have messages
    /// written with the v11 Microsoft.Azure.Storage.Queue SDK.
    /// </summary>
    Legacy = 1
}

public class AzureStorageQueueOptions<T> : SharedQueueOptions<T> where T : class
{
    public string ConnectionString { get; set; }

    /// <summary>
    /// The interval to wait between polling for new messages when the queue is empty.
    /// </summary>
    public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A function that returns the delay before retrying a failed message based on the attempt number.
    /// Default is exponential backoff with jitter: 2^attempt seconds + random 0-100ms.
    /// </summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = attempt =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));

    /// <summary>
    /// Controls message format compatibility.
    /// Default mode uses an envelope wrapper that supports CorrelationId and Properties.
    /// Legacy mode is provided for backward compatibility with existing queues that have messages
    /// written with the v11 Microsoft.Azure.Storage.Queue SDK (uses Base64 encoding and raw payload).
    ///
    /// <para><b>Default:</b> AzureStorageQueueCompatibilityMode.Default (envelope with metadata)</para>
    ///
    /// <para><b>Migration from v11 SDK:</b></para>
    /// <list type="number">
    ///   <item><description>Set CompatibilityMode = Legacy to read existing v11 messages</description></item>
    ///   <item><description>Process all existing messages from the queue</description></item>
    ///   <item><description>Once queue is empty, switch to CompatibilityMode = Default</description></item>
    ///   <item><description>All new messages will use envelope format with metadata support</description></item>
    /// </list>
    /// </summary>
    public AzureStorageQueueCompatibilityMode CompatibilityMode { get; set; } = AzureStorageQueueCompatibilityMode.Default;

    /// <summary>
    /// Optional action to configure Azure SDK retry options.
    /// Default Azure SDK retry settings: MaxRetries=3, Delay=0.8s, MaxDelay=1min, Mode=Exponential.
    /// </summary>
    /// <example>
    /// <code>
    /// options.ConfigureRetry = retry =>
    /// {
    ///     retry.MaxRetries = 5;
    ///     retry.Delay = TimeSpan.FromSeconds(1);
    ///     retry.Mode = RetryMode.Exponential;
    /// };
    /// </code>
    /// </example>
    public Action<RetryOptions> ConfigureRetry { get; set; }
}

public class AzureStorageQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, AzureStorageQueueOptions<T>, AzureStorageQueueOptionsBuilder<T>> where T : class
{
    public AzureStorageQueueOptionsBuilder<T> ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        Target.ConnectionString = connectionString;
        return this;
    }

    public AzureStorageQueueOptionsBuilder<T> DequeueInterval(TimeSpan dequeueInterval)
    {
        Target.DequeueInterval = dequeueInterval;
        return this;
    }

    /// <summary>
    /// Sets a custom retry delay function for failed messages.
    /// </summary>
    /// <param name="retryDelay">A function that takes the attempt number and returns the delay before the next retry.</param>
    public AzureStorageQueueOptionsBuilder<T> RetryDelay(Func<int, TimeSpan> retryDelay)
    {
        ArgumentNullException.ThrowIfNull(retryDelay);

        Target.RetryDelay = retryDelay;
        return this;
    }

    /// <summary>
    /// Sets the message format compatibility mode.
    /// See <see cref="AzureStorageQueueOptions{T}.CompatibilityMode"/> for migration guidance.
    /// </summary>
    public AzureStorageQueueOptionsBuilder<T> CompatibilityMode(AzureStorageQueueCompatibilityMode compatibilityMode)
    {
        Target.CompatibilityMode = compatibilityMode;
        return this;
    }

    /// <summary>
    /// Configures Azure SDK retry options for transient failure handling.
    /// </summary>
    /// <param name="configure">Action to configure retry options.</param>
    /// <example>
    /// <code>
    /// .ConfigureRetry(retry =>
    /// {
    ///     retry.MaxRetries = 5;
    ///     retry.Delay = TimeSpan.FromSeconds(1);
    /// })
    /// </code>
    /// </example>
    public AzureStorageQueueOptionsBuilder<T> ConfigureRetry(Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Target.ConfigureRetry = configure;
        return this;
    }
}
