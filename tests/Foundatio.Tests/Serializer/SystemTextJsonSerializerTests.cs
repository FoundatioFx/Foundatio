using System.Text.Json;
using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer;

public class SystemTextJsonSerializerWithOptionsTests : SerializerTestsBase
{
    public SystemTextJsonSerializerWithOptionsTests(ITestOutputHelper output) : base(output) { }

    protected override ISerializer GetSerializer()
    {
        return new SystemTextJsonSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper });
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

public class SystemTextJsonSerializerTests : SerializerTestsBase
{
    public SystemTextJsonSerializerTests(ITestOutputHelper output) : base(output) { }

    protected override ISerializer GetSerializer()
    {
        return new SystemTextJsonSerializer();
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

public class SystemTextJsonSerializerBenchmark : SerializerBenchmarkBase
{
    protected override ISerializer GetSerializer()
    {
        return new SystemTextJsonSerializer();
    }
}
