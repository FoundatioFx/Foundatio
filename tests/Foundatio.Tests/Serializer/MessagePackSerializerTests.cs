using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer
{
    public class MessagePackSerializerTests : SerializerTestsBase
    {
        public MessagePackSerializerTests(ITestOutputHelper output) : base(output) { }

        protected override ISerializer GetSerializer()
        {
            return new MessagePackSerializer();
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
            var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<MessagePackSerializerBenchmark>();
            _logger.LogInformation(summary.ToJson());
        }
    }

    public class MessagePackSerializerBenchmark : SerializerBenchmarkBase
    {
        protected override ISerializer GetSerializer()
        {
            return new MessagePackSerializer();
        }
    }
}
