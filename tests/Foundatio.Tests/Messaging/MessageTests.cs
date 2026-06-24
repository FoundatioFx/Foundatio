using System;
using System.Runtime.InteropServices;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessageTests
{
    [Fact]
    public void Constructor_WithByteArray_StoresDataAsReadOnlyMemory()
    {
        byte[] payload = [1, 2, 3, 4];

        var message = new Message(payload, _ => null);

        Assert.False(message.Data.IsEmpty);
        Assert.Equal(payload.Length, message.Data.Length);
        Assert.True(payload.AsSpan().SequenceEqual(message.Data.Span));
    }

    [Fact]
    public void Constructor_WithReadOnlyMemory_StoresDataWithoutCopy()
    {
        byte[] payload = [10, 20, 30];
        var memory = new ReadOnlyMemory<byte>(payload);

        var message = new Message(memory, _ => null);

        Assert.Equal(3, message.Data.Length);
        Assert.True(payload.AsSpan().SequenceEqual(message.Data.Span));

        // Verify the data was stored without copying: the stored memory must still be backed by the
        // exact same array instance and segment as the original payload, not a defensive copy.
        Assert.True(MemoryMarshal.TryGetArray(message.Data, out ArraySegment<byte> segment));
        Assert.Same(payload, segment.Array);
        Assert.Equal(0, segment.Offset);
        Assert.Equal(payload.Length, segment.Count);
    }

    [Fact]
    public void Data_WhenEmptyMemory_IsEmptyReturnsTrue()
    {
        var message = new Message(ReadOnlyMemory<byte>.Empty, _ => null);

        Assert.True(message.Data.IsEmpty);
        Assert.Equal(0, message.Data.Length);
    }

    [Fact]
    public void GetBody_InvokesProvidedDelegate()
    {
        byte[] payload = [1];
        var expected = new object();

        var message = new Message(payload, _ => expected);

        Assert.Same(expected, message.GetBody());
    }

    [Fact]
    public void TypedMessage_ExposesUnderlyingData()
    {
        byte[] payload = [5, 6, 7];
        var inner = new Message(payload, _ => "body")
        {
            Type = "test",
            UniqueId = "id",
            CorrelationId = "corr"
        };

        var typed = new Message<string>(inner);

        Assert.Equal("body", typed.Body);
        Assert.Equal("test", typed.Type);
        Assert.Equal("id", typed.UniqueId);
        Assert.Equal("corr", typed.CorrelationId);
        Assert.True(payload.AsSpan().SequenceEqual(typed.Data.Span));
    }
}
