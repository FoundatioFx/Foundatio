using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using Foundatio.Serializer;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer {
    public class MessagePackSerializerTests : SerializerTestsBase {
        public MessagePackSerializerTests(ITestOutputHelper output) : base(output) { }

        protected override ISerializer GetSerializer() {
            return new MessagePackSerializer();
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
            var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<MessagePackSerializerBenchmark>();
            _logger.LogInformation(summary.ToJson());
        }
    }

    public class MessagePackSerializerBenchmark : SerializerBenchmarkBase {
        protected override ISerializer GetSerializer() {
            return new MessagePackSerializer();
        }
    }
}