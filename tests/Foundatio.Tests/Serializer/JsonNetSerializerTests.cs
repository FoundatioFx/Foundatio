using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer;

public class JsonNetSerializerTests : SerializerTestsBase
{
    public JsonNetSerializerTests(ITestOutputHelper output) : base(output) { }

    protected override ISerializer GetSerializer()
    {
        return new JsonNetSerializer();
    }

    [Fact]
    public override void CanRoundTripBytes()
    {
        base.CanRoundTripBytes();
    }

    [Fact]
    public override void CanRoundTripString()
    {
        base.CanRoundTripString();
    }

    [Fact]
    public override void CanHandlePrimitiveTypes()
    {
        base.CanHandlePrimitiveTypes();
    }

    [Fact(Skip = "Skip benchmarks for now")]
    public virtual void Benchmark()
    {
        var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<JsonNetSerializerBenchmark>();
        _logger.LogInformation(summary.ToJson());
    }
}

public class JsonNetSerializerBenchmark : SerializerBenchmarkBase
{
    protected override ISerializer GetSerializer()
    {
        return new JsonNetSerializer();
    }
}
