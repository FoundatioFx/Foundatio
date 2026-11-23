using System;
using System.Reflection;
using Azure.Storage.Queues;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureStorage.Tests.Messaging;

public class AzureStorageQueueMessageTransportOptionsTests
{
    [Theory]
    [InlineData("AccountKey")]
    [InlineData("SharedAccessSignature")]
    [InlineData(" accountkey ")]
    public void ToString_WithASecretInTheConnectionString_RedactsOnlyTheSecret(string secretName)
    {
        // Arrange
        var options = new AzureStorageQueueMessageTransportOptions
        {
            ConnectionString = $"DefaultEndpointsProtocol=https;AccountName=myaccount;{secretName}=c2VjcmV0LWtleQ==;EndpointSuffix=core.windows.net",
            Prefix = "dev-"
        };

        // Act
        string text = options.ToString();

        // Assert
        Assert.DoesNotContain("c2VjcmV0LWtleQ", text);
        Assert.Contains($"{secretName}=***", text);
        Assert.Contains("AccountName=myaccount;", text);
        foreach (var property in typeof(AzureStorageQueueMessageTransportOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Contains($"{property.Name} = ", text);
    }

    [Fact]
    public void ToString_WithASasInTheServiceUri_LeavesItOut()
    {
        var options = new AzureStorageQueueMessageTransportOptions { ServiceUri = new Uri("https://myaccount.queue.core.windows.net/?sv=2024-01-01&sig=c2VjcmV0") };

        string text = options.ToString();

        Assert.DoesNotContain("sig=", text);
        Assert.Contains("https://myaccount.queue.core.windows.net/", text);
    }

    [Theory]
    [InlineData("Dev-")]
    [InlineData("-dev")]
    [InlineData("dev--x")]
    [InlineData("dev.x")]
    [InlineData("a-prefix-longer-than-16")]
    public void Constructor_WithAnInvalidPrefix_Throws(string prefix)
    {
        var options = new AzureStorageQueueMessageTransportOptions { ConnectionString = "UseDevelopmentStorage=true", Prefix = prefix };

        Assert.Throws<ArgumentException>(() => new AzureStorageQueueMessageTransport(options));
    }

    [Fact]
    public void Constructor_WithPollIntervalsOutOfOrder_Throws()
    {
        var options = new AzureStorageQueueMessageTransportOptions { ConnectionString = "UseDevelopmentStorage=true", MinPollInterval = TimeSpan.FromSeconds(5), MaxPollInterval = TimeSpan.FromSeconds(1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new AzureStorageQueueMessageTransport(options));
    }

    [Fact]
    public void Constructor_WithoutAConnection_ThrowsAndSaysWhatToSet()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AzureStorageQueueMessageTransport(new AzureStorageQueueMessageTransportOptions()));

        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ServiceUri", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithAConnectionStringAndAServiceUri_ThrowsInsteadOfPickingOne()
    {
        var options = new AzureStorageQueueMessageTransportOptions { ConnectionString = "UseDevelopmentStorage=true", ServiceUri = new Uri("https://myaccount.queue.core.windows.net") };

        var ex = Assert.Throws<ArgumentException>(() => new AzureStorageQueueMessageTransport(options));

        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("prod-")]
    public void GetQueueName_WithAPrefix_SeparatesItAndKeepsTheTopicAndSubscriptionReadable(string prefix)
    {
        var transport = new AzureStorageQueueMessageTransport(new QueueServiceClient("UseDevelopmentStorage=true"), new AzureStorageQueueMessageTransportOptions { Prefix = prefix });

        Assert.Matches("^prod-orders-[0-9a-f]{8}$", transport.GetQueueName(MessageSource.Queue("orders")));
        Assert.Matches("^prod-shop-orders-[0-9a-f]{8}-billing-[0-9a-f]{8}$", transport.GetQueueName(MessageSource.Subscription("shop-orders", "billing")));
        Assert.Equal(transport.GetQueueName(MessageSource.Queue("orders")) + "-poison", transport.GetQueueName(MessageSource.DeadLetter(MessageSource.Queue("orders"))));
        Assert.Throws<ArgumentException>(() => transport.GetQueueName(MessageSource.InstanceSubscription("shop-orders", "web")));
    }

    [Fact]
    public void GetQueueName_WithTheLongestPrefixAndNames_StaysWithinStorageLimits()
    {
        var transport = new AzureStorageQueueMessageTransport(new QueueServiceClient("UseDevelopmentStorage=true"), new AzureStorageQueueMessageTransportOptions { Prefix = "abcdefghijklmnop" });
        string longName = new('x', 200);

        foreach (var source in new[] { MessageSource.Queue(longName), MessageSource.Subscription(longName, longName), MessageSource.Subscription("t", longName) })
        {
            string deadLetter = transport.GetQueueName(MessageSource.DeadLetter(source));
            Assert.True(deadLetter.Length <= 63, $"{deadLetter} is {deadLetter.Length} characters.");
            Assert.Matches("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", deadLetter);
        }
    }

    [Fact]
    public void Capabilities_DeclareStorageQueuesLimits()
    {
        var transport = new AzureStorageQueueMessageTransport(new QueueServiceClient("UseDevelopmentStorage=true"));

        var capabilities = transport.Capabilities;

        Assert.Equal(TimeSpan.FromDays(7), capabilities.MaxDelay);
        Assert.Equal(TimeSpan.FromDays(7), capabilities.MaxRetryDelay);
        Assert.Equal(TimeSpan.FromDays(7), capabilities.MaxLeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), capabilities.MinRetryDelay);
        Assert.Equal(32, capabilities.MaxReceiveBatchSize);
        Assert.Equal(46 * 1024, capabilities.MaxMessageSize);
        Assert.True(capabilities.ReceiveAhead);
        Assert.False(capabilities.OrderedGroups);
        Assert.False(capabilities.Deduplication);
    }
}
