using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisHybridCacheClientTests : HybridCacheClientTests {
        public RedisHybridCacheClientTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        protected override ICacheClient GetCacheClient() {
            return new RedisHybridCacheClient(SharedConnection.GetMuxer(), loggerFactory: Log);
        }

        [Fact]
        public override Task CanSetAndGetValueAsync() {
            return base.CanSetAndGetValueAsync();
        }

        [Fact]
        public override Task CanSetAndGetObjectAsync() {
            return base.CanSetAndGetObjectAsync();
        }
        
        [Fact]
        public override Task CanTryGetAsync() {
            return base.CanTryGetAsync();
        }

        [Fact]
        public override Task CanRemoveByPrefixAsync() {
            return base.CanRemoveByPrefixAsync();
        }

        [Fact]
        public override Task CanUseScopedCachesAsync() {
            return base.CanUseScopedCachesAsync();
        }

        [Fact]
        public override Task CanSetExpirationAsync() {
            return base.CanSetExpirationAsync();
        }
        
        [Fact]
        public override Task CanManageSetsAsync() {
            return base.CanManageSetsAsync();
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
        public override Task WillWorkWithSets() {
            return base.WillWorkWithSets();
        }

        [Fact(Skip = "Performance Test")]
        public override Task MeasureThroughputAsync() {
            return base.MeasureThroughputAsync();
        }

        [Fact(Skip = "Performance Test")]
        public override Task MeasureSerializerSimpleThroughputAsync() {
            return base.MeasureSerializerSimpleThroughputAsync();
        }

        [Fact(Skip = "Performance Test")]
        public override Task MeasureSerializerComplexThroughputAsync() {
            return base.MeasureSerializerComplexThroughputAsync();
        }
    }
}
