using System;
using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer {
    public class Utf8JsonSerializerTests : SerializerTestsBase {
        public Utf8JsonSerializerTests(ITestOutputHelper output) : base(output) { }

        protected override ISerializer GetSerializer() {
            return new Utf8JsonSerializer();
        }
        
        [Fact]
        public override void CanRoundTripBytes() {
            base.CanRoundTripBytes();
        }
        
        [Fact]
        public override void CanRoundTripString() {
            base.CanRoundTripString();
        }

        [Fact]
        public virtual void Benchmark() {
            var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<Utf8JsonSerializerBenchmark>();
            _logger.LogInformation(summary.ToJson());
        }
    }

    public class Utf8JsonSerializerBenchmark : SerializerBenchmarkBase {
        protected override ISerializer GetSerializer() {
            return new Utf8JsonSerializer();
        }
    }
}