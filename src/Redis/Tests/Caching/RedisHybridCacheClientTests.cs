using Foundatio.Caching;
using Foundatio.Tests.Caching;
using Xunit;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisHybridCacheClientTests : HybridCacheClientTests {
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
