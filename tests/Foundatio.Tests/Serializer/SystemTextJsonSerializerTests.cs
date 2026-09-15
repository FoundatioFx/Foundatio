using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Serializer;

public class SystemTextJsonSerializerTests : SerializerTestsBase
{
    public SystemTextJsonSerializerTests(ITestOutputHelper output) : base(output) { }

    protected override ISerializer GetSerializer()
    {
        return new SystemTextJsonSerializer();
    }

    [Fact]
    public void SerializeToBytes_LargePayload_DoesNotAllocateIntermediatePayloadBuffers()
    {
        ISerializer serializer = new SystemTextJsonSerializer();
        string value = new('x', 50_000);
        for (int i = 0; i < 10; i++) serializer.SerializeToBytes(value);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long length = 0;
        for (int i = 0; i < 100; i++) length += serializer.SerializeToBytes(value).Length;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(5_000_200, length);
        Assert.True(allocated < length * 1.25, $"Allocated {allocated:N0} bytes for {length:N0} bytes of output.");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("2147483648")]
    [InlineData("12.5")]
    [InlineData("true")]
    [InlineData("\"héllo 世界\"")]
    [InlineData("\"2026-09-07T00:00:00+03:00\"")]
    [InlineData("{\"value\":42}")]
    [InlineData("[1,2,3]")]
    [InlineData("\uFEFF42")]
    [InlineData("\uFEFF{\"value\":42}")]
    public void Deserialize_BytesAndSlicedMemory_MatchesStreamNormalization(string json)
    {
        ISerializer serializer = new SystemTextJsonSerializer();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(bytes);
        object? expected = serializer.Deserialize(stream, typeof(object));
        byte[] padded = Encoding.UTF8.GetBytes("invalid" + json + "invalid");
        ReadOnlyMemory<byte> slice = padded.AsMemory(7, bytes.Length);

        AssertEquivalent(expected, serializer.Deserialize<object>(bytes));
        AssertEquivalent(expected, serializer.Deserialize(bytes, typeof(object)));
        AssertEquivalent(expected, serializer.Deserialize<object>(slice));
        AssertEquivalent(expected, serializer.Deserialize(slice, typeof(object)));

        static void AssertEquivalent(object? expected, object? actual)
        {
            Assert.Equal(expected?.GetType(), actual?.GetType());
            if (expected is JsonElement element) Assert.Equal(element.GetRawText(), ((JsonElement)actual!).GetRawText());
            else Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SerializeToBytes_CustomOptionsAndRuntimeType_MatchesStream()
    {
        var writeOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
        var readOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        ISerializer serializer = new SystemTextJsonSerializer(writeOptions, readOptions);
        object value = new BufferTestMessage("héllo 世界", 42);
        using var stream = new MemoryStream();
        serializer.Serialize(value, stream);

        byte[] bytes = serializer.SerializeToBytes(value);

        Assert.Equal(stream.ToArray(), bytes);
        Assert.Equal(value, serializer.Deserialize<BufferTestMessage>(bytes));
        Assert.Equal(value, serializer.Deserialize<BufferTestMessage>(bytes.AsMemory()));
    }

    public sealed record BufferTestMessage(string DisplayName, int MessageCount);

    [Fact]
    public override void Deserialize_WithInvalidArguments_ThrowsArgumentNullException()
    {
        base.Deserialize_WithInvalidArguments_ThrowsArgumentNullException();
    }

    [Fact]
    public override void Deserialize_WithInvalidInput_ThrowsArgumentException()
    {
        base.Deserialize_WithInvalidInput_ThrowsArgumentException();
    }

    [Fact]
    public override void Deserialize_WithPrimitiveType_ReturnsValue()
    {
        base.Deserialize_WithPrimitiveType_ReturnsValue();
    }

    [Fact]
    public override void Deserialize_WithUnicodeAndSpecialCharacters_PreservesContent()
    {
        base.Deserialize_WithUnicodeAndSpecialCharacters_PreservesContent();
    }

    [Fact]
    public override void Deserialize_WithValidBytes_ReturnsDeserializedObject()
    {
        base.Deserialize_WithValidBytes_ReturnsDeserializedObject();
    }

    [Fact]
    public override void Deserialize_WithValidStream_ReturnsDeserializedObject()
    {
        base.Deserialize_WithValidStream_ReturnsDeserializedObject();
    }

    [Fact]
    public override void Deserialize_WithValidString_ReturnsDeserializedObject()
    {
        base.Deserialize_WithValidString_ReturnsDeserializedObject();
    }

    [Fact]
    public override void Serialize_WithDateTimeValue_PreservesValue()
    {
        base.Serialize_WithDateTimeValue_PreservesValue();
    }

    [Fact]
    public override void Serialize_WithEmptyCollection_ReturnsValidOutput()
    {
        base.Serialize_WithEmptyCollection_ReturnsValidOutput();
    }

    [Fact]
    public override void Serialize_WithInvalidArguments_ThrowsArgumentNullException()
    {
        base.Serialize_WithInvalidArguments_ThrowsArgumentNullException();
    }

    [Fact]
    public override void Serialize_WithNullPropertyInObject_HandlesCorrectly()
    {
        base.Serialize_WithNullPropertyInObject_HandlesCorrectly();
    }

    [Fact]
    public override void Serialize_WithNullValue_RoundTripsCorrectly()
    {
        base.Serialize_WithNullValue_RoundTripsCorrectly();
    }

    [Fact]
    public override void Serialize_WithNumericTypes_PreservesValues()
    {
        base.Serialize_WithNumericTypes_PreservesValues();
    }

    [Fact]
    public override void Serialize_WithSpecialCharacters_RoundTripsCorrectly()
    {
        base.Serialize_WithSpecialCharacters_RoundTripsCorrectly();
    }

    [Fact]
    public override void Deserialize_WithNumericPrimitivesToObject_ReturnsCorrectTypes()
    {
        base.Deserialize_WithNumericPrimitivesToObject_ReturnsCorrectTypes();
    }

    [Fact(Skip = "Skip benchmarks for now")]
    public virtual void Benchmark()
    {
        var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<SystemTextJsonSerializerBenchmark>();
        _logger.LogInformation(summary.ToJson());
    }
}

public class SystemTextJsonSerializerWithOptionsTests : SerializerTestsBase
{
    public SystemTextJsonSerializerWithOptionsTests(ITestOutputHelper output) : base(output) { }

    protected override ISerializer GetSerializer()
    {
        return new SystemTextJsonSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper });
    }

    [Fact]
    public override void Deserialize_WithInvalidArguments_ThrowsArgumentNullException()
    {
        base.Deserialize_WithInvalidArguments_ThrowsArgumentNullException();
    }

    [Fact]
    public override void Deserialize_WithInvalidInput_ThrowsArgumentException()
    {
        base.Deserialize_WithInvalidInput_ThrowsArgumentException();
    }

    [Fact]
    public override void Deserialize_WithPrimitiveType_ReturnsValue()
    {
        base.Deserialize_WithPrimitiveType_ReturnsValue();
    }

    [Fact]
    public override void Deserialize_WithUnicodeAndSpecialCharacters_PreservesContent()
    {
        base.Deserialize_WithUnicodeAndSpecialCharacters_PreservesContent();
    }

    [Fact]
    public override void Deserialize_WithValidBytes_ReturnsDeserializedObject()
    {
        base.Deserialize_WithValidBytes_ReturnsDeserializedObject();
    }

    [Fact]
    public override void Deserialize_WithValidStream_ReturnsDeserializedObject()
    {
        base.Deserialize_WithValidStream_ReturnsDeserializedObject();
    }

    [Fact]
    public override void Deserialize_WithValidString_ReturnsDeserializedObject()
    {
        base.Deserialize_WithValidString_ReturnsDeserializedObject();
    }

    [Fact]
    public override void Serialize_WithDateTimeValue_PreservesValue()
    {
        base.Serialize_WithDateTimeValue_PreservesValue();
    }

    [Fact]
    public override void Serialize_WithEmptyCollection_ReturnsValidOutput()
    {
        base.Serialize_WithEmptyCollection_ReturnsValidOutput();
    }

    [Fact]
    public override void Serialize_WithInvalidArguments_ThrowsArgumentNullException()
    {
        base.Serialize_WithInvalidArguments_ThrowsArgumentNullException();
    }

    [Fact]
    public override void Serialize_WithNullPropertyInObject_HandlesCorrectly()
    {
        base.Serialize_WithNullPropertyInObject_HandlesCorrectly();
    }

    [Fact]
    public override void Serialize_WithNullValue_RoundTripsCorrectly()
    {
        base.Serialize_WithNullValue_RoundTripsCorrectly();
    }

    [Fact]
    public override void Serialize_WithNumericTypes_PreservesValues()
    {
        base.Serialize_WithNumericTypes_PreservesValues();
    }

    [Fact]
    public override void Serialize_WithSpecialCharacters_RoundTripsCorrectly()
    {
        base.Serialize_WithSpecialCharacters_RoundTripsCorrectly();
    }

    [Fact]
    public override void Deserialize_WithNumericPrimitivesToObject_ReturnsCorrectTypes()
    {
        base.Deserialize_WithNumericPrimitivesToObject_ReturnsCorrectTypes();
    }
}

public class SystemTextJsonSerializerBenchmark : SerializerBenchmarkBase
{
    protected override ISerializer GetSerializer()
    {
        return new SystemTextJsonSerializer();
    }
}
