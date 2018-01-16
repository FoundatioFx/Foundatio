using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisCacheClientTests : CacheClientTestsBase {
        public RedisCacheClientTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        protected override ICacheClient GetCacheClient() {
            return new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = SharedConnection.GetMuxer(), LoggerFactory = Log });
        }

        [Fact]
        public override Task CanGetAllAsync() {
            return base.CanGetAllAsync();
        }

        [Fact]
        public override Task CanGetAllWithOverlapAsync() {
            return base.CanGetAllWithOverlapAsync();
        }

        [Fact]
        public override Task CanSetAsync() {
            return base.CanSetAsync();
        }

        [Fact]
        public override Task CanSetAndGetValueAsync() {
            return base.CanSetAndGetValueAsync();
        }

        [Fact]
        public override Task CanAddAsync() {
            return base.CanAddAsync();
        }

        [Fact]
        public override Task CanAddConcurrentlyAsync() {
            return base.CanAddConcurrentlyAsync();
        }

        [Fact]
        public override Task CanTryGetAsync() {
            return base.CanTryGetAsync();
        }

        [Fact]
        public override Task CanUseScopedCachesAsync() {
            return base.CanUseScopedCachesAsync();
        }

        [Fact]
        public override Task CanSetAndGetObjectAsync() {
            return base.CanSetAndGetObjectAsync();
        }

        [Fact]
        public override Task CanRemoveByPrefixAsync() {
            return base.CanRemoveByPrefixAsync();
        }

        [Fact]
        public override Task CanSetExpirationAsync() {
            return base.CanSetExpirationAsync();
        }

        [Fact]
        public override Task CanIncrementAsync() {
            return base.CanIncrementAsync();
        }

        [Fact]
        public override Task CanIncrementAndExpireAsync() {
            return base.CanIncrementAndExpireAsync();
        }

        [Fact]
        public override Task CanGetAndSetDateTimeAsync() {
            return base.CanGetAndSetDateTimeAsync();
        }

        [Fact]
        public override Task CanRoundTripLargeNumbersAsync() {
            return base.CanRoundTripLargeNumbersAsync();
        }

        [Fact]
        public override Task CanRoundTripLargeNumbersWithExpirationAsync() {
            return base.CanRoundTripLargeNumbersWithExpirationAsync();
        }

        [Fact]
        public override Task CanManageSetsAsync() {
            return base.CanManageSetsAsync();
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
