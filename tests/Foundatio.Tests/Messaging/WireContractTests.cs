using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class WireContractTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task SendAsync_WithInterfaceContract_PreservesConcreteWireType(bool publish, int batchKind)
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        var registry = new MessageTypeRegistry([new("changed.v1", typeof(Changed))]);
        await using var bus = new MessageBus(transport, new() { MessageTypes = registry, OwnsTransport = false });
        var received = new TaskCompletionSource<IChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = publish
            ? await bus.SubscribeAsync<IChange>((m, _) => { received.TrySetResult(m.Message); return Task.CompletedTask; }, new() { Topic = "changes", Subscription = "audit" }, token)
            : await bus.ConsumeAsync<IChange>((m, _) => { received.TrySetResult(m.Message); return Task.CompletedTask; }, new() { Destination = "changes" }, token);
        IChange change = new Changed(42);
        if (publish)
        {
            var options = new MessagePublishOptions { Topic = "changes" };
            if (batchKind == 0) await bus.PublishAsync(change, options, token);
            else if (batchKind == 1) await bus.PublishBatchAsync<IChange>([change], options, token);
            else await bus.PublishBatchAsync<IChange>([new MessageBatchItem<IChange>(change, "change-42")], options, token);
        }
        else
        {
            var options = new MessageSendOptions { Destination = "changes" };
            if (batchKind == 0) await bus.SendAsync(change, options, token);
            else if (batchKind == 1) await bus.SendBatchAsync<IChange>([change], options, token);
            else await bus.SendBatchAsync<IChange>([new MessageBatchItem<IChange>(change, "change-42")], options, token);
        }

        Assert.Equal(42, Assert.IsType<Changed>(await received.Task.WaitAsync(TimeSpan.FromSeconds(2), token)).Id);
    }

    public interface IChange { int Id { get; } }
    public sealed record Changed(int Id) : IChange;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveAsync_WithObjectContract_ResolvesRegisteredConcreteType(bool listener)
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new()
        {
            OwnsTransport = false,
            MessageTypes = new MessageTypeRegistry([new("changed.v1", typeof(Changed))])
        });
        await bus.SendAsync(new Changed(42), new() { Destination = "changes" }, token);
        if (listener)
        {
            var received = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = await bus.ConsumeAsync<object>((m, _) => { received.TrySetResult(m.Message); return Task.CompletedTask; }, new() { Destination = "changes" }, token);
            Assert.Equal(42, Assert.IsType<Changed>(await received.Task.WaitAsync(TimeSpan.FromSeconds(2), token)).Id);
        }
        else
        {
            await using var received = await bus.ReceiveAsync<object>(new() { Destination = "changes" }, token);
            Assert.Equal(42, Assert.IsType<Changed>(received!.Message).Id);
            await received.CompleteAsync(token);
        }
    }

    [Fact]
    public void MessageTypeRegistry_ResolvesOnlyExplicitlyRegisteredTypes()
    {
        var registry = new MessageTypeRegistry(new[] { new MessageTypeRegistration("work.v1", typeof(Work)) });
        Assert.Equal(typeof(Work), registry.Resolve("work.v1"));
        Assert.Null(registry.Resolve(typeof(Work).AssemblyQualifiedName!));
        Assert.Null(new MessageTypeRegistry().Resolve(typeof(Work).FullName!));
    }

    [Fact]
    public async Task ReceiveAsync_WithIncompatibleContentType_DeadLettersWithoutDeserializingAsync()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        var queue = DestinationAddress.ForQueue("work");
        await transport.SendAsync(queue, new[] { new TransportMessage { Body = new byte[] { 0xff, 0x80 }, ContentType = "application/octet-stream" } }, new TransportSendOptions(), token);
        await using var bus = new MessageBus(transport);
        await Assert.ThrowsAsync<MessageBusException>(() => bus.ReceiveAsync<Work>(new MessageReceiveOptions { Destination = "work" }, token));
        var dead = Assert.Single(await transport.PeekDeadLetteredAsync(queue, new DeadLetterQuery(), token));
        Assert.Equal("unsupported-content-type", dead.Headers[KnownHeaders.DeadLetterReason]);
        Assert.Equal("application/octet-stream", dead.ContentType);
    }

    [Fact]
    public async Task SendBatchAsync_WhenSecondChunkFails_ReportsEveryInputOutcomeAsync()
    {
        var transport = new Mock<IMessageTransport>();
        transport.As<ITransportInfo>().SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        transport.As<ITransportInfo>().Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities { MaxBatchSize = 1 });
        transport.SetupSequence(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult { Items = new[] { new SendItemResult { MessageId = "broker-id" } } })
            .ThrowsAsync(new TimeoutException("Acceptance is unknown"));
        await using var bus = new MessageBus(transport.Object);
        var error = await Assert.ThrowsAsync<MessageSendException>(() => bus.SendBatchAsync(new[] { new Work(), new Work(), new Work() }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new[] { MessageSendStatus.Accepted, MessageSendStatus.Unknown, MessageSendStatus.NotAttempted }, error.Outcomes.Select(o => o.Status));
        Assert.Equal(3, error.Outcomes.Select(o => o.MessageId).Distinct().Count());
        Assert.All(error.Outcomes, o => Assert.NotEmpty(o.MessageId));
        transport.Verify(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendAsync_WhenBrokerAssignsAnotherId_ReturnsApplicationIdAsync()
    {
        var sent = new List<TransportMessage>();
        await using var bus = new MessageBus(CreateTransport(sent));
        string id = await bus.SendAsync(new Work(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Assert.Single(sent).MessageId, id);
        Assert.NotEqual("broker-id", id);
        Assert.Equal(id, sent[0].Headers[KnownHeaders.MessageId]);
    }

    [Fact]
    public async Task SendBatchAsync_WhenBrokerAssignsOtherIds_PreservesApplicationIdsAsync()
    {
        var sent = new List<TransportMessage>();
        await using var bus = new MessageBus(CreateTransport(sent));
        var ids = await bus.SendBatchAsync(new[] { new Work(), new Work() }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sent.Select(m => m.MessageId), ids);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task SendAsync_WithCallerId_PreservesIdAcrossAttemptsAsync()
    {
        var sent = new List<TransportMessage>();
        await using var bus = new MessageBus(CreateTransport(sent));
        var options = new MessageSendOptions { MessageId = "order-123" };
        Assert.Equal("order-123", await bus.SendAsync(new Work(), options, TestContext.Current.CancellationToken));
        Assert.Equal("order-123", await bus.SendAsync(new Work(), options, TestContext.Current.CancellationToken));
        Assert.All(sent, m => Assert.Equal("order-123", m.MessageId));
    }

    [Fact]
    public async Task SendAsync_WithBinarySerializer_AdvertisesBinaryBodyAsync()
    {
        var sent = new List<TransportMessage>();
        await using var bus = new MessageBus(CreateTransport(sent), new MessageBusOptions { Serializer = new BinarySerializer() });
        await bus.SendAsync(new Work(), cancellationToken: TestContext.Current.CancellationToken);
        var message = Assert.Single(sent);
        Assert.Equal("application/octet-stream", message.ContentType);
        Assert.Equal(message.ContentType, message.Headers[KnownHeaders.ContentType]);
        Assert.Equal(new byte[] { 0xff, 0x80, 0x00 }, message.Body.ToArray());
    }

    [Fact]
    public void MessageContext_WithApplicationHeader_SeparatesApplicationAndBrokerIds()
    {
        var entry = new TransportEntry
        {
            Id = "broker-id",
            Destination = DestinationAddress.ForQueue("work"),
            Body = ReadOnlyMemory<byte>.Empty,
            Headers = MessageHeaders.Empty.ToBuilder().Set(KnownHeaders.MessageId, "order-123").Build(),
            Receipt = default
        };
        var context = new MessageContext(Mock.Of<IMessageTransport>(), entry, CancellationToken.None);
        Assert.Equal("order-123", context.Id);
        Assert.Equal("broker-id", context.BrokerMessageId);
    }

    private static IMessageTransport CreateTransport(List<TransportMessage> sent)
    {
        var transport = new Mock<IMessageTransport>();
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress _, IReadOnlyList<TransportMessage> messages, TransportSendOptions _, CancellationToken _) =>
            {
                sent.AddRange(messages);
                return Task.FromResult(new SendResult { Items = messages.Select(_ => new SendItemResult { MessageId = "broker-id" }).ToArray() });
            });
        return transport.Object;
    }

    private sealed class Work;

    private sealed class BinarySerializer : ISerializer
    {
        public object? Deserialize(Stream data, Type objectType) => throw new NotSupportedException();
        public void Serialize(object? value, Stream output) => output.Write(new byte[] { 0xff, 0x80, 0x00 });
    }
}
