using System;
using System.Collections.Generic;
using System.Linq;
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
    [InlineData("application/json", "{\"name\":\"héllo 世界\"}")]
    [InlineData("application/octet-stream", "binary")]
    [InlineData(null, "unknown")]
    public async Task SendAndReceiveAsync_CompactEnvelope_PreservesPayloadMetadataAndNativeFilters(string? contentType, string text)
    {
        var token = TestContext.Current.CancellationToken;
        var body = contentType == "application/octet-stream" ? Enumerable.Range(0, 256).Select(i => (byte)i).ToArray() : Encoding.UTF8.GetBytes(text);
        var headers = MessageHeaders.Create(new Dictionary<string, string>
        {
            [KnownHeaders.MessageType] = "order.v1",
            [KnownHeaders.Priority] = "high",
            [KnownHeaders.CorrelationId] = "trace-id",
            [KnownHeaders.MessageId] = "independent-header-id",
            [KnownHeaders.ContentType] = "independent-header-type",
            ["Mixed-Case"] = "résumé"
        });
        SendMessageBatchRequestEntry? sent = null;
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "http://test/queue" });
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageBatchRequest request, CancellationToken _) =>
            {
                sent = Assert.Single(request.Entries);
                return new SendMessageBatchResponse { Successful = [new SendMessageBatchResultEntry { Id = sent.Id, MessageId = "broker-id" }] };
            });
        sqs.Setup(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ReceiveMessageResponse { Messages = [new Message { MessageId = "broker-id", ReceiptHandle = "receipt", Body = sent!.MessageBody, MessageAttributes = sent.MessageAttributes }] });
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        await transport.SendAsync(DestinationAddress.ForQueue("test"), [new TransportMessage { Body = body, ContentType = contentType, MessageId = "application-id", Headers = headers }], new(), token);
        Assert.Equal(4, sent!.MessageAttributes.Count);
        Assert.Contains("fnd.envelope", sent.MessageAttributes.Keys);
        foreach (string key in new[] { KnownHeaders.MessageType, KnownHeaders.Priority, KnownHeaders.CorrelationId })
            Assert.Equal(headers[key], sent.MessageAttributes[key].StringValue);
        if (contentType == "application/json") Assert.Equal(text, sent.MessageBody);
        var received = Assert.Single(await transport.ReceiveAsync(DestinationAddress.ForQueue("test"), new(), token));
        Assert.Null(received.EnvelopeError);
        Assert.Equal(body, received.Body.ToArray());
        Assert.Equal("application-id", received.ApplicationMessageId);
        Assert.Equal(contentType, received.ContentType);
        Assert.Equal("broker-id", received.Id);
        Assert.Equal(headers.Count, received.Headers.Count);
        foreach (var header in headers) Assert.Equal(header.Value, received.Headers[header.Key]);
        Assert.Equal("résumé", received.Headers["mixed-case"]);
    }

    [Theory]
    [InlineData("fnd.headers", "{invalid", "original body")]
    [InlineData("fnd.encoding", "base64", "!!!")]
    [InlineData("fnd.envelope", "{invalid", "dmFsaWQ=")]
    [InlineData("fnd.envelope", "{\"Version\":2,\"Encoding\":\"text\",\"Headers\":{}}", "dmFsaWQ=")]
    [InlineData("fnd.envelope", "{\"Version\":1,\"Encoding\":\"unknown\",\"Headers\":{}}", "dmFsaWQ=")]
    [InlineData("fnd.envelope", "{\"Version\":1,\"Encoding\":\"text\",\"Headers\":null}", "dmFsaWQ=")]
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
