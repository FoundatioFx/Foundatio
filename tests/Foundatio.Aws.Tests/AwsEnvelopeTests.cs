using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Foundatio.Messaging;
using Moq;
using Xunit;

namespace Foundatio.Aws.Tests;

public class AwsEnvelopeTests
{
    [Theory]
    [InlineData("fnd.headers", "{invalid", "original body")]
    [InlineData("fnd.encoding", "base64", "!!!")]
    public async Task ReceiveAsync_MalformedEnvelope_PreservesReceiptAndValidEntries(string attribute, string value, string body)
    {
        var token = TestContext.Current.CancellationToken;
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "http://test/queue" });
        sqs.Setup(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReceiveMessageResponse
        {
            Messages = [
                new Message { MessageId = "bad", ReceiptHandle = "bad-receipt", Body = body, MessageAttributes = new Dictionary<string, MessageAttributeValue> { [attribute] = new() { StringValue = value, DataType = "String" } } },
                new Message { MessageId = "good", ReceiptHandle = "good-receipt", Body = "valid", MessageAttributes = new Dictionary<string, MessageAttributeValue> { ["fnd.encoding"] = new() { StringValue = "text", DataType = "String" } } }
            ]
        });
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var entries = await transport.ReceiveAsync(DestinationAddress.ForQueue("test"), new ReceiveRequest { MaxMessages = 2 }, token);
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].EnvelopeError);
        Assert.Equal(body, Encoding.UTF8.GetString(entries[0].Body.Span));
        Assert.Equal("bad-receipt", entries[0].Receipt.TransportState);
        Assert.Null(entries[1].EnvelopeError);
        Assert.Equal("valid", Encoding.UTF8.GetString(entries[1].Body.Span));
    }
}
