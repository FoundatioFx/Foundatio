using System;
using Foundatio.Serializer;
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
    }
}