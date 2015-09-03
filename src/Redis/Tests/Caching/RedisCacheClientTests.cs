using System.Collections.Generic;
using Foundatio.Caching;
using Foundatio.Metrics;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisCacheClientTests : CacheClientTestsBase {
        private readonly TestOutputWriter _writer;

        public RedisCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            _writer = new TestOutputWriter(output);
        }

        protected override ICacheClient GetCacheClient() {
            return new RedisCacheClient(SharedConnection.GetMuxer());
        }

        [Fact]
        public override void CanSetAndGetValue() {
            base.CanSetAndGetValue();
        }

        [Fact]
        public override void CanSetAndGetObject() {
            base.CanSetAndGetObject();
        }

        [Fact]
        public override void CanSetExpiration() {
            base.CanSetExpiration();
        }


        [Fact]
        public override void CanRemoveByPrefix() {
            base.CanRemoveByPrefix();
        }

        [Fact]
        public override void CanUseScopedCaches() {
            base.CanUseScopedCaches();
        }

        [Fact]
        public override void MeasureThroughput() {
            base.MeasureThroughput();
        }

        [Fact]
        public override void MeasureSerializerSimpleThroughput() {
            base.MeasureSerializerSimpleThroughput();
        }

        [Fact]
        public override void MeasureSerializerComplexThroughput() {
            base.MeasureSerializerComplexThroughput();
        }
    }
}
