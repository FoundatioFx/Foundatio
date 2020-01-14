using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer {
    public class CompressedMessagePackSerializerTests : SerializerTestsBase {
        public CompressedMessagePackSerializerTests(ITestOutputHelper output) : base(output) { }

        protected override ISerializer GetSerializer() {
            return new MessagePackSerializer(MessagePack.MessagePackSerializerOptions.Standard
                .WithCompression(MessagePack.MessagePackCompression.Lz4Block)
                .WithResolver(ContractlessStandardResolver.Instance));
        }

        [Fact]
        public override void CanRoundTripBytes() {
            base.CanRoundTripBytes();
        }

        [Fact]
        public override void CanRoundTripString() {
            base.CanRoundTripString();
        }

        [Fact(Skip = "Skip benchmarks for now")]
        public virtual void Benchmark() {
            var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<CompressedMessagePackSerializerBenchmark>();
            _logger.LogInformation(summary.ToJson());
        }
    }

    public class CompressedMessagePackSerializerBenchmark : SerializerBenchmarkBase {
        protected override ISerializer GetSerializer() {
            return new MessagePackSerializer(MessagePack.MessagePackSerializerOptions.Standard
                .WithCompression(MessagePack.MessagePackCompression.Lz4Block)
                .WithResolver(ContractlessStandardResolver.Instance));
        }
    }
}
