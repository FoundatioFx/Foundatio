using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisHybridCacheClientTests : HybridCacheClientTests {
        public RedisHybridCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ICacheClient GetCacheClient() {
            return new RedisHybridCacheClient(SharedConnection.GetMuxer());
        }

        [Fact]
        public override Task CanSetAndGetValue() {
            return base.CanSetAndGetValue();
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
        public override Task CanRemoveByPrefix() {
            return base.CanRemoveByPrefix();
        }

        [Fact]
        public override Task CanUseScopedCaches() {
            return base.CanUseScopedCaches();
        }

        [Fact]
        public override Task CanSetExpiration() {
            return base.CanSetExpiration();
        }

        [Fact]
        public override Task WillUseLocalCache() {
            return base.WillUseLocalCache();
        }

        [Fact]
        public override Task WillExpireRemoteItems() {
            return base.WillExpireRemoteItems();
        }

        [Fact]
        public override void MeasureThroughput()
        {
            base.MeasureThroughput();
        }

        [Fact]
        public override void MeasureSerializerSimpleThroughput()
        {
            base.MeasureSerializerSimpleThroughput();
        }

        [Fact]
        public override void MeasureSerializerComplexThroughput()
        {
            base.MeasureSerializerComplexThroughput();
        }
    }
}
