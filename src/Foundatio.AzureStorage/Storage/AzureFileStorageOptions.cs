using System;
using Azure.Core;

namespace Foundatio.Storage;

public class AzureFileStorageOptions : SharedOptions
{
    public string ConnectionString { get; set; }
    public string ContainerName { get; set; } = "storage";

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

public class AzureFileStorageOptionsBuilder : SharedOptionsBuilder<AzureFileStorageOptions, AzureFileStorageOptionsBuilder>
{
    public AzureFileStorageOptionsBuilder ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        Target.ConnectionString = connectionString;
        return this;
    }

    public AzureFileStorageOptionsBuilder ContainerName(string containerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerName);

        Target.ContainerName = containerName;
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
    public AzureFileStorageOptionsBuilder ConfigureRetry(Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Target.ConfigureRetry = configure;
        return this;
    }
}
