using Foundatio.Caching;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisHybridCacheClientTests : HybridCacheClientTests {
        public RedisHybridCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override ICacheClient GetCacheClient() {
            return new RedisHybridCacheClient(SharedConnection.GetMuxer());
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
        public override void CanRemoveByPrefix() {
            base.CanRemoveByPrefix();
        }

        [Fact]
        public override void CanUseScopedCaches() {
            base.CanUseScopedCaches();
        }

        [Fact]
        public override void CanSetExpiration() {
            base.CanSetExpiration();
        }

        [Fact]
        public override void WillUseLocalCache() {
            base.WillUseLocalCache();
        }

        [Fact]
        public override void WillExpireRemoteItems() {
            base.WillExpireRemoteItems();
        }
    }
}
