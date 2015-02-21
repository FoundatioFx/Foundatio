using Foundatio.Caching;
using Foundatio.Redis.Cache;
using Foundatio.Tests.Caching;
using Foundatio.Tests.Utility;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisHybridCacheClientTests : HybridCacheClientTests {
        protected override ICacheClient GetCacheClient() {
            if (ConnectionStrings.Get("RedisConnectionString") == null)
                return null;

            var muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));
            return new RedisHybridCacheClient(muxer);
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
