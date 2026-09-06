using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Foundatio.Messaging.Benchmarks;

public static class AwsResources
{
    public static string? ServiceUrl => Environment.GetEnvironmentVariable("PERF_AWS_MODE") == "live" ? null : Environment.GetEnvironmentVariable("PERF_AWS_URL") ?? "http://localhost:24566";
    public static RegionEndpoint Region => RegionEndpoint.GetBySystemName(Environment.GetEnvironmentVariable("PERF_AWS_REGION") ?? "us-east-1");
    public static AWSCredentials? LocalCredentials => ServiceUrl is null ? null : new BasicAWSCredentials("test", "test");
    public static AmazonSQSConfig SqsConfig
    {
        get
        {
            var config = new AmazonSQSConfig { RegionEndpoint = Region };
            if (ServiceUrl is { } url) { config.ServiceURL = url; config.AuthenticationRegion = Region.SystemName; }
            return config;
        }
    }
    public static AmazonSimpleNotificationServiceConfig SnsConfig
    {
        get
        {
            var config = new AmazonSimpleNotificationServiceConfig { RegionEndpoint = Region };
            if (ServiceUrl is { } url) { config.ServiceURL = url; config.AuthenticationRegion = Region.SystemName; }
            return config;
        }
    }

    public static async Task CleanupAsync(string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var sqs = LocalCredentials is { } credentials ? new AmazonSQSClient(credentials, SqsConfig) : new AmazonSQSClient(SqsConfig);
        using var sns = LocalCredentials is { } snsCredentials ? new AmazonSimpleNotificationServiceClient(snsCredentials, SnsConfig) : new AmazonSimpleNotificationServiceClient(SnsConfig);
        string? next = null;
        do
        {
            var queues = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix, NextToken = next, MaxResults = 1000 }, timeout.Token);
            foreach (string queue in queues.QueueUrls ?? []) await sqs.DeleteQueueAsync(queue, timeout.Token);
            next = queues.NextToken;
        } while (next is not null);
        do
        {
            var topics = await sns.ListTopicsAsync(next, timeout.Token);
            foreach (var topic in topics.Topics ?? [])
                if (topic.TopicArn[(topic.TopicArn.LastIndexOf(':') + 1)..].StartsWith(prefix, StringComparison.Ordinal))
                    await sns.DeleteTopicAsync(topic.TopicArn, timeout.Token);
            next = topics.NextToken;
        } while (next is not null);
    }
}
