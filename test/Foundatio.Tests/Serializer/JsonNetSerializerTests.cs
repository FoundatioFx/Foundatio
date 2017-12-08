using System;
using Foundatio.Serializer;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Serializer {
    public class JsonNetSerializerTests : SerializerTestsBase {
        public JsonNetSerializerTests(ITestOutputHelper output) : base(output) { }

        protected override ISerializer GetSerializer() {
            return new JsonNetSerializer();
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