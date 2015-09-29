using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisCacheClientTests : CacheClientTestsBase {
        public RedisCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ICacheClient GetCacheClient(string channelName = null) {
            return new RedisCacheClient(SharedConnection.GetMuxer());
        }

        [Fact]
        public override Task CanGetAll() {
            return base.CanGetAll();
        }

        [Fact]
        public override Task CanSetAndGetValue() {
            return base.CanSetAndGetValue();
        }
        
        [Fact]
        public override Task CanAddConncurrently() {
            return base.CanAddConncurrently();
        }

        [Fact]
        public override Task CanSetAndGetObject() {
            return base.CanSetAndGetObject();
        }
        
        [Fact]
        public override Task CanTryGet() {
            return base.CanTryGet();
        }

        [Fact]
        public override Task CanSetExpiration() {
            return base.CanSetExpiration();
        }


        [Fact]
        public override Task CanRemoveByPrefix() {
            return base.CanRemoveByPrefix();
        }

        [Fact]
        public override Task CanUseScopedCaches() {
            return base.CanUseScopedCaches();
        }

        [Fact(Skip = "Performance Test")]
        public override Task MeasureThroughput() {
            return base.MeasureThroughput();
        }

        [Fact(Skip = "Performance Test")]
        public override Task MeasureSerializerSimpleThroughput() {
            return base.MeasureSerializerSimpleThroughput();
        }

        [Fact(Skip = "Performance Test")]
        public override Task MeasureSerializerComplexThroughput() {
            return base.MeasureSerializerComplexThroughput();
        }
    }
}
