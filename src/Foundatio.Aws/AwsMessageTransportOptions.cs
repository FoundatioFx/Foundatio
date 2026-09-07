using System;
using Amazon;
using Amazon.Runtime;

namespace Foundatio.Messaging;

public class AwsMessageTransportOptions
{
    /// <summary>AWS credentials. When null, the SDK's default credential chain is used.</summary>
    public AWSCredentials? Credentials { get; set; }

    /// <summary>AWS region. When null, the SDK's default region resolution is used (ignored when <see cref="ServiceUrl"/> is set).</summary>
    public RegionEndpoint? Region { get; set; }

    /// <summary>Custom service endpoint, e.g. <c>http://localhost:4566</c> for LocalStack.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Optional prefix applied to the underlying SQS queue and SNS topic names (not the logical destination names used
    /// by callers). Useful to isolate runs/environments on a shared broker — e.g. a unique prefix per conformance run
    /// so leftover messages from a prior run can't leak in.
    /// </summary>
    public string ResourcePrefix { get; set; } = "";

    /// <summary>Default receive visibility timeout when none is supplied. Maps to the SQS visibility window.</summary>
    public TimeSpan DefaultVisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Coalesce concurrent single sends, publishes and acknowledgements into native AWS batches.</summary>
    public bool EnableBatching { get; set; } = true;

    /// <summary>Maximum time to collect a partial batch; idle single-operation streams dispatch immediately. Zero batches only operations already waiting.</summary>
    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>Maximum concurrent automatically collected requests per destination and operation (send or acknowledge).</summary>
    public int MaxConcurrentBatches { get; set; } = 4;

    /// <summary>Maximum buffered operations per destination and operation. Further callers await capacity.</summary>
    public int MaxPendingBatchMessages { get; set; } = 100;

    /// <summary>Timeout for a shared AWS batch request and for draining batchers during transport disposal.</summary>
    public TimeSpan BatchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchDelay, TimeSpan.FromMilliseconds(100));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentBatches, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrentBatches, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPendingBatchMessages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPendingBatchMessages, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BatchTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchTimeout, TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Parses a connection string of the form
    /// <c>serviceurl=http://localhost:4566;accesskey=...;secretkey=...;region=us-east-1</c> into options. Any subset of
    /// keys may be provided; unknown keys are ignored.
    /// </summary>
    public static AwsMessageTransportOptions FromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        string? accessKey = null, secretKey = null, region = null, serviceUrl = null;
        foreach (string pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator < 0)
                continue;

            string key = pair[..separator].Trim().ToLowerInvariant().Replace(" ", "");
            string value = pair[(separator + 1)..].Trim();

            switch (key)
            {
                case "accesskey":
                case "accesskeyid":
                case "id":
                    accessKey = value;
                    break;
                case "secretkey":
                case "secret":
                    secretKey = value;
                    break;
                case "region":
                case "endpoint":
                    region = value;
                    break;
                case "serviceurl":
                case "service":
                    serviceUrl = value;
                    break;
            }
        }

        var options = new AwsMessageTransportOptions { ServiceUrl = serviceUrl };
        if (!String.IsNullOrEmpty(accessKey) && !String.IsNullOrEmpty(secretKey))
            options.Credentials = new BasicAWSCredentials(accessKey, secretKey);
        if (!String.IsNullOrEmpty(region))
            options.Region = RegionEndpoint.GetBySystemName(region);

        return options;
    }
}
