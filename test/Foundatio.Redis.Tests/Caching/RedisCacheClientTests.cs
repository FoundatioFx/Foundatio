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
            return new RedisCacheClient(SharedConnection.GetMuxer(), loggerFactory: Log);
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
        public override Task CanAdd() {
            return base.CanAdd();
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
        public override Task CanIncrementAndExpire() {
            return base.CanIncrementAndExpire();
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
        public override async Task CanManageSets() {
            await base.CanManageSets();
        }

        [Fact]
        public override Task CanGetOrAddAsync()
        {
            return base.CanGetOrAddAsync();
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
