using System;
using System.Collections.Concurrent;
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
using Sns = Amazon.SimpleNotificationService.Model;

namespace Foundatio.Aws.Tests;

public class AwsBatchTests
{
    [Fact]
    public async Task SendAsync_AutomaticBatcher_DoesNotRetainCallerExecutionContext()
    {
        var caller = new AsyncLocal<string?> { Value = "first-request" };
        var observed = new ConcurrentQueue<string?>();
        var sqs = CreateSqs();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageBatchRequest request, CancellationToken _) =>
            {
                observed.Enqueue(caller.Value);
                return Accepted(request);
            });
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        await transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("one")], new(), TestContext.Current.CancellationToken);
        caller.Value = "second-request";
        await transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("two")], new(), TestContext.Current.CancellationToken);
        Assert.Equal(2, observed.Count);
        Assert.All(observed, Assert.Null);
        Assert.Equal("second-request", caller.Value);
    }

    [Fact]
    public async Task SendAsync_LargerExplicitBatch_PreservesIndicesAcrossRequestsAndCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sqs = CreateSqs();
        int calls = 0;
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageBatchRequest request, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref calls) == 2) cancellation.Cancel();
                return Accepted(request);
            });
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var result = await transport.SendAsync(DestinationAddress.ForQueue("test"), Enumerable.Range(0, 25).Select(i => Text(i.ToString())).ToArray(), new(), cancellation.Token);
        Assert.Equal(2, calls);
        for (int i = 0; i < 25; i++)
        {
            Assert.Equal(i, result.Items[i].Index);
            Assert.Equal(i < 20 ? MessageSendStatus.Accepted : MessageSendStatus.NotAttempted, result.Items[i].Status);
            if (i < 20) Assert.Equal("broker-" + i, result.Items[i].MessageId);
        }
    }

    [Fact]
    public async Task SendAsync_OversizedEncodedMessage_RejectsOnlyThatInput()
    {
        var sqs = CreateSqs();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageBatchRequest request, CancellationToken _) => Accepted(request));
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var result = await transport.SendAsync(DestinationAddress.ForQueue("test"), [Text(new string('é', 600_000)), Text("valid")], new(), TestContext.Current.CancellationToken);
        Assert.Equal(MessageSendStatus.Rejected, result.Items[0].Status);
        Assert.Equal("MessageTooLarge", result.Items[0].ErrorCode);
        Assert.Equal(MessageSendStatus.Accepted, result.Items[1].Status);
        Assert.Equal(1, result.Items[1].Index);
        Assert.Equal("broker-valid", result.Items[1].MessageId);
        sqs.Verify(s => s.SendMessageBatchAsync(It.Is<SendMessageBatchRequest>(r => r.Entries.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 1, 0, 100)]
    [InlineData(1, 0, 0, 100)]
    [InlineData(1, 1, -1, 100)]
    [InlineData(1, 1, 101, 100)]
    [InlineData(1, 1, 0, 0)]
    public void Constructor_InvalidBatchLimits_RejectsConfiguration(int concurrency, int pending, int delay, int timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AwsMessageTransport(new AwsMessageTransportOptions
        {
            MaxConcurrentBatches = concurrency,
            MaxPendingBatchMessages = pending,
            BatchDelay = TimeSpan.FromMilliseconds(delay),
            BatchTimeout = TimeSpan.FromMilliseconds(timeout)
        }));
    }

    [Fact]
    public async Task SendAsync_ConcurrentTopics_RespectsByteLimitAndPreservesOutcomes()
    {
        var requests = new ConcurrentBag<Sns.PublishBatchRequest>();
        var sns = new Mock<IAmazonSimpleNotificationService>();
        sns.Setup(s => s.ListTopicsAsync(It.IsAny<Sns.ListTopicsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Sns.ListTopicsResponse { Topics = [new Sns.Topic { TopicArn = "arn:aws:sns:us-east-1:123:test" }] });
        sns.Setup(s => s.PublishBatchAsync(It.IsAny<Sns.PublishBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Sns.PublishBatchRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return new Sns.PublishBatchResponse
                {
                    Successful = request.PublishBatchRequestEntries.Where(e => e.Message[0] != 'c').Select(e => new Sns.PublishBatchResultEntry { Id = e.Id, MessageId = "broker-" + e.Message[0] }).ToList(),
                    Failed = request.PublishBatchRequestEntries.Where(e => e.Message[0] == 'c').Select(e => new Sns.BatchResultErrorEntry { Id = e.Id, Code = "InvalidParameter", SenderFault = true }).ToList()
                };
            });
        await using var transport = new AwsMessageTransport(new() { BatchDelay = TimeSpan.FromMilliseconds(20) }, Mock.Of<IAmazonSQS>(), sns.Object);
        var tasks = Enumerable.Range(0, 6).Select(i => transport.SendAsync(DestinationAddress.ForTopic("test"),
            [Text(new string((char)('a' + i), 100_000))], new(), TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.InRange(requests.Count, 3, 5);
        Assert.All(requests, request => Assert.InRange(request.PublishBatchRequestEntries.Count, 1, 2));
        for (int i = 0; i < results.Length; i++)
        {
            var item = Assert.Single(results[i].Items);
            Assert.Equal(0, item.Index);
            Assert.Equal(i == 2 ? MessageSendStatus.Rejected : MessageSendStatus.Accepted, item.Status);
            if (i == 2) Assert.False(item.Retryable);
            else Assert.Equal("broker-" + (char)('a' + i), item.MessageId);
        }
    }

    [Fact]
    public async Task SendAsync_ConcurrentDestinationsAndDelays_DoesNotMixTheirSettings()
    {
        var requests = new ConcurrentBag<SendMessageBatchRequest>();
        var sqs = CreateSqs();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageBatchRequest request, CancellationToken _) => { requests.Add(request); return Accepted(request); });
        await using var transport = new AwsMessageTransport(new() { BatchDelay = TimeSpan.FromMilliseconds(20) }, sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var tasks = Enumerable.Range(0, 20).Select(i => transport.SendAsync(DestinationAddress.ForQueue(i % 2 == 0 ? "even" : "odd"),
            [Text(i.ToString())], new() { DeliverAt = i % 4 == 0 ? DateTimeOffset.UtcNow.AddSeconds(60) : null }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.InRange(requests.Count, 2, 6);
        foreach (var request in requests)
            foreach (var entry in request.Entries)
            {
                int number = Int32.Parse(entry.MessageBody);
                Assert.Equal(number % 2 == 0 ? "http://test/even" : "http://test/odd", request.QueueUrl);
                if (number % 4 == 0) Assert.InRange(entry.DelaySeconds.GetValueOrDefault(), 55, 60);
                else Assert.Null(entry.DelaySeconds);
            }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task CompleteAsync_UnconfirmedReceipt_DoesNotReportSuccess(string outcome)
    {
        var sqs = CreateSqs();
        sqs.Setup(s => s.DeleteMessageBatchAsync(It.IsAny<DeleteMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeleteMessageBatchRequest request, CancellationToken _) => new DeleteMessageBatchResponse
            {
                Successful = request.Entries.Where(e => e.ReceiptHandle != "bad" || outcome == "duplicate").Select(e => new DeleteMessageBatchResultEntry { Id = e.Id }).ToList(),
                Failed = request.Entries.Where(e => e.ReceiptHandle == "bad" && outcome != "missing").Select(e => new BatchResultErrorEntry { Id = e.Id, Code = "ReceiptHandleIsInvalid", SenderFault = true }).ToList()
            });
        await using var transport = new AwsMessageTransport(new(), sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var bad = transport.CompleteAsync(Entry("bad"), TestContext.Current.CancellationToken);
        var good = transport.CompleteAsync(Entry("good"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<MessageBusException>(() => bad);
        if (outcome == "duplicate")
        {
            try { await good; }
            catch (MessageBusException) { }
        }
        else await good;
    }

    [Fact]
    public async Task SendAsync_CancelOneInFlightCaller_DoesNotCancelOtherMessages()
    {
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SendMessageBatchRequest? sharedRequest = null;
        CancellationToken sharedToken = default;
        var sqs = CreateSqs();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendMessageBatchRequest request, CancellationToken ct) =>
            {
                if (request.Entries[0].MessageBody == "occupy")
                {
                    occupied.TrySetResult();
                    await allowBatch.Task.WaitAsync(ct);
                    return Accepted(request);
                }
                sharedRequest = request;
                sharedToken = ct;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Accepted(request);
            });
        await using var transport = new AwsMessageTransport(new() { MaxConcurrentBatches = 1 }, sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var occupying = transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("occupy")], new(), TestContext.Current.CancellationToken);
        await occupied.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var first = transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("first")], new(), canceled.Token);
        var second = transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("second")], new(), TestContext.Current.CancellationToken);
        try
        {
            allowBatch.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(["first", "second"], sharedRequest!.Entries.Select(e => e.MessageBody));
            await canceled.CancelAsync();
            var canceledResult = await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(MessageSendStatus.Unknown, Assert.Single(canceledResult.Items).Status);
            Assert.False(second.IsCompleted);
            Assert.False(sharedToken.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(MessageSendStatus.Accepted, Assert.Single((await occupying).Items).Status);
        Assert.Equal(MessageSendStatus.Accepted, Assert.Single((await second).Items).Status);
    }

    [Fact]
    public async Task SendAsync_BoundedQueueAndDisposal_DropsCanceledWorkAndDrainsAcceptedWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentBag<string>();
        var sqs = CreateSqs();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendMessageBatchRequest request, CancellationToken ct) =>
            {
                foreach (var entry in request.Entries) sent.Add(entry.MessageBody);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Accepted(request);
            });
        await using var transport = new AwsMessageTransport(new() { MaxConcurrentBatches = 1, MaxPendingBatchMessages = 1, BatchDelay = TimeSpan.Zero }, sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("first")], new(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("second")], new(), TestContext.Current.CancellationToken);
        var third = transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("canceled")], new(), canceled.Token);
        Task disposal;
        try
        {
            await canceled.CancelAsync();
            Assert.NotEqual(MessageSendStatus.Accepted, Assert.Single((await third).Items).Status);
            Assert.Single(sent);
            disposal = transport.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(MessageSendStatus.Accepted, Assert.Single((await first).Items).Status);
        Assert.Equal(MessageSendStatus.Accepted, Assert.Single((await second).Items).Status);
        Assert.Equal(new[] { "first", "second" }, sent.Order());
        sqs.Verify(s => s.Dispose(), Times.Never);
    }

    [Fact]
    public async Task SendAsync_SharedRequestTimeout_ReportsUnknownAndAllowsLaterRequests()
    {
        var sqs = CreateSqs();
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendMessageBatchRequest request, CancellationToken ct) =>
            {
                if (request.Entries[0].MessageBody == "timeout")
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Accepted(request);
            });
        await using var transport = new AwsMessageTransport(new() { BatchTimeout = TimeSpan.FromMilliseconds(100) }, sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var first = await transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("timeout")], new(), TestContext.Current.CancellationToken);
        Assert.Equal(MessageSendStatus.Unknown, Assert.Single(first.Items).Status);
        var second = await transport.SendAsync(DestinationAddress.ForQueue("test"), [Text("success")], new(), TestContext.Current.CancellationToken);
        Assert.Equal(MessageSendStatus.Accepted, Assert.Single(second.Items).Status);
    }

    private static Mock<IAmazonSQS> CreateSqs()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string queue, CancellationToken _) => new GetQueueUrlResponse { QueueUrl = "http://test/" + queue });
        return sqs;
    }

    private static TransportMessage Text(string value) => new() { Body = System.Text.Encoding.UTF8.GetBytes(value), ContentType = "text/plain" };
    private static TransportEntry Entry(string receipt) => new() { Id = receipt, Destination = DestinationAddress.ForQueue("test"), Body = ReadOnlyMemory<byte>.Empty, Receipt = new Receipt { TransportState = receipt } };
    private static SendMessageBatchResponse Accepted(SendMessageBatchRequest request) => new()
    {
        Successful = request.Entries.Select(e => new SendMessageBatchResultEntry { Id = e.Id, MessageId = "broker-" + e.MessageBody }).ToList()
    };

    [Fact]
    public async Task SendAsync_ConcurrentSingleMessages_CoalescesRequestsAndPreservesEachOutcome()
    {
        var requests = new ConcurrentBag<SendMessageBatchRequest>();
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "http://test/queue" });
        sqs.Setup(s => s.SendMessageBatchAsync(It.IsAny<SendMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageBatchRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return new SendMessageBatchResponse
                {
                    Successful = request.Entries.Where(e => e.MessageBody != "7").Select(e => new SendMessageBatchResultEntry { Id = e.Id, MessageId = "broker-" + e.MessageBody }).ToList(),
                    Failed = request.Entries.Where(e => e.MessageBody == "7").Select(e => new BatchResultErrorEntry { Id = e.Id, Code = "Throttled", SenderFault = false }).ToList()
                };
            });
        await using var transport = new AwsMessageTransport(new() { BatchDelay = TimeSpan.FromMilliseconds(20) }, sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var tasks = Enumerable.Range(0, 20).Select(i => transport.SendAsync(DestinationAddress.ForQueue("test"),
            [new TransportMessage { Body = System.Text.Encoding.UTF8.GetBytes(i.ToString()), ContentType = "text/plain" }], new(), TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.InRange(requests.Count, 2, 5);
        Assert.All(requests, request => Assert.InRange(request.Entries.Count, 1, 10));
        for (int i = 0; i < results.Length; i++)
        {
            var item = Assert.Single(results[i].Items);
            Assert.Equal(0, item.Index);
            Assert.Equal(i == 7 ? MessageSendStatus.Rejected : MessageSendStatus.Accepted, item.Status);
            if (i == 7) Assert.True(item.Retryable);
            else Assert.Equal("broker-" + i, item.MessageId);
        }
    }

    [Fact]
    public async Task CompleteAsync_ConcurrentReceipts_WaitsForBatchedBrokerAcknowledgement()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentBag<DeleteMessageBatchRequest>();
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.GetQueueUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "http://test/queue" });
        sqs.Setup(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DeleteMessageResponse());
        sqs.Setup(s => s.DeleteMessageBatchAsync(It.IsAny<DeleteMessageBatchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (DeleteMessageBatchRequest request, CancellationToken ct) =>
            {
                requests.Add(request);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new DeleteMessageBatchResponse { Successful = request.Entries.Select(e => new DeleteMessageBatchResultEntry { Id = e.Id }).ToList() };
            });
        await using var transport = new AwsMessageTransport(new() { BatchDelay = TimeSpan.FromMilliseconds(20) }, sqs.Object, Mock.Of<IAmazonSimpleNotificationService>());
        var tasks = Enumerable.Range(0, 20).Select(i => transport.CompleteAsync(new TransportEntry
        {
            Id = i.ToString(),
            Destination = DestinationAddress.ForQueue("test"),
            Body = ReadOnlyMemory<byte>.Empty,
            Receipt = new Receipt { TransportState = "receipt-" + i }
        }, TestContext.Current.CancellationToken)).ToArray();
        try
        {
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.InRange(requests.Count, 2, 5);
        Assert.All(requests, request => Assert.InRange(request.Entries.Count, 1, 10));
        Assert.Equal(20, requests.Sum(r => r.Entries.Count));
        sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
