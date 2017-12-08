using System;
using Foundatio.Serializer;
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
    }
}