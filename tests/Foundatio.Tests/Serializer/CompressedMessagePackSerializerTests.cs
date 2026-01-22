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
