using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Foundatio.Messaging;
using Moq;
using Xunit;

namespace Foundatio.Aws.Tests;

public class AwsNodeSubscriptionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OpenNodeSubscriptionAsync_ProvisioningFailsAfterQueueCreation_DeletesQueue(bool policyFails, bool cleanupSnsFails)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var failure = new InvalidOperationException("Provisioning failed.");
        var sqs = new Mock<IAmazonSQS>();
        sqs.SetupSequence(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueDoesNotExistException("Missing."))
            .ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "http://test/node" });
        sqs.Setup(s => s.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateQueueResponse { QueueUrl = "http://test/node" });
        sqs.Setup(s => s.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { Attributes = new() { ["QueueArn"] = "arn:aws:sqs:us-east-1:123:node" } });
        var policy = sqs.Setup(s => s.SetQueueAttributesAsync(It.IsAny<SetQueueAttributesRequest>(), It.IsAny<CancellationToken>()));
        if (policyFails) policy.ThrowsAsync(failure);
        else policy.ReturnsAsync(new SetQueueAttributesResponse());
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(s => s.CreateTopicAsync(It.IsAny<CreateTopicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTopicResponse { TopicArn = "arn:aws:sns:us-east-1:123:events" });
        sns.Setup(s => s.SubscribeAsync(It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var cleanupTopics = sns.Setup(s => s.ListTopicsAsync(It.IsAny<ListTopicsRequest>(), It.IsAny<CancellationToken>()));
        if (cleanupSnsFails) cleanupTopics.ThrowsAsync(new InvalidOperationException("SNS remains unavailable during cleanup."));
        else cleanupTopics.ReturnsAsync(new ListTopicsResponse());
        await using var transport = new AwsMessageTransport(new(), sqs.Object, sns.Object);

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenNodeSubscriptionAsync(new() { Topic = "events", NodeId = "node" }, token));

        // Assert
        Assert.Same(failure, thrown);
        sqs.Verify(s => s.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        sqs.Verify(s => s.DeleteQueueAsync("http://test/node", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Nodes_ReceiveIndependentCopies_AndDisposeTheirResources()
    {
        string? connection = Environment.GetEnvironmentVariable("FOUNDATIO_AWS_CONNECTION_STRING");
        Assert.SkipWhen(String.IsNullOrEmpty(connection), "FOUNDATIO_AWS_CONNECTION_STRING not set.");
        var options = AwsMessageTransportOptions.FromConnectionString(connection);
        options.ResourcePrefix = $"node-test-{Guid.NewGuid():N}-";
        await using var transport = new AwsMessageTransport(options);
        await using var bus = new MessageBus(transport, new MessageBusOptions { OwnsTransport = false });
        var token = TestContext.Current.CancellationToken;
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await bus.SubscribeNodeAsync((_, _) => { firstReceived.TrySetResult(); return Task.CompletedTask; }, new() { Topic = "events", NodeId = "first" }, token);
        var second = await bus.SubscribeNodeAsync((_, _) => { secondReceived.TrySetResult(); return Task.CompletedTask; }, new() { Topic = "events", NodeId = "second" }, token);
        try
        {
            Assert.NotEqual(first.Source, second.Source);
            await bus.PublishAsync("changed", new MessagePublishOptions { Topic = "events" }, token);
            await Task.WhenAll(firstReceived.Task, secondReceived.Task).WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            await transport.DeleteAsync(DestinationAddress.ForTopic("events"), token);
        }
        Assert.False(await transport.ExistsAsync(first.Source, token));
        Assert.False(await transport.ExistsAsync(second.Source, token));
    }
}
