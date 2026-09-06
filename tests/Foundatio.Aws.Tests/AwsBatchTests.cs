using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Foundatio.Messaging;
using Moq;
using Xunit;

namespace Foundatio.Aws.Tests;

public class AwsBatchTests
{
    [Fact]
    public async Task SendAsync_NativeBatch_ReportsNoncontiguousFailure()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "http://test/queue" });
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SendMessageBatchResponse
        {
            Successful = [new SendMessageBatchResultEntry { Id = "2", MessageId = "broker-c" }, new SendMessageBatchResultEntry { Id = "0", MessageId = "broker-a" }],
            Failed = [new BatchResultErrorEntry { Id = "1", Code = "Throttled", SenderFault = false, Message = "Retry later" }]
        });
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var result = await transport.SendAsync(DestinationAddress.ForQueue("test"), Enumerable.Range(0, 3).Select(_ => new TransportMessage { Body = "hello"u8.ToArray(), ContentType = "text/plain" }).ToArray(), new(), TestContext.Current.CancellationToken);
        Assert.Collection(result.Items,
            a => Assert.Equal(MessageSendStatus.Accepted, a.Status),
            b => { Assert.Equal(MessageSendStatus.Rejected, b.Status); Assert.Equal(1, b.Index); Assert.True(b.Retryable); },
            c => Assert.Equal(MessageSendStatus.Accepted, c.Status));
        sqs.Verify(s => s.SendMessageBatchAsync(It.Is<SendMessageBatchRequest>(r => r.Entries.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
        sqs.Verify(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
