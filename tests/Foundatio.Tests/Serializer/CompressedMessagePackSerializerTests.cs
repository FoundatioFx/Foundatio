using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Serializer;

public class CompressedMessagePackSerializerTests : SerializerTestsBase
{
    public CompressedMessagePackSerializerTests(ITestOutputHelper output) : base(output) { }

    protected override ISerializer GetSerializer()
    {
        return new MessagePackSerializer(MessagePack.MessagePackSerializerOptions.Standard
            .WithCompression(MessagePack.MessagePackCompression.Lz4Block)
            .WithResolver(ContractlessStandardResolver.Instance));
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
    public override void SerializeToBytes_WithNullValue_ReturnsNull()
    {
        base.SerializeToBytes_WithNullValue_ReturnsNull();
    }

    [Fact]
    public override void SerializeToString_WithNullValue_ReturnsNull()
    {
        base.SerializeToString_WithNullValue_ReturnsNull();
    }

    [Fact(Skip = "Skip benchmarks for now")]
    public virtual void Benchmark()
    {
        var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<CompressedMessagePackSerializerBenchmark>();
        _logger.LogInformation(summary.ToJson());
    }
}

public class CompressedMessagePackSerializerBenchmark : SerializerBenchmarkBase
{
    protected override ISerializer GetSerializer()
    {
        return new MessagePackSerializer(MessagePack.MessagePackSerializerOptions.Standard
            .WithCompression(MessagePack.MessagePackCompression.Lz4Block)
            .WithResolver(ContractlessStandardResolver.Instance));
    }
}
