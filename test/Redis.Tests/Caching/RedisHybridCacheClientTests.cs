using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Tests.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching {
    public class RedisHybridCacheClientTests : HybridCacheClientTests {
        public RedisHybridCacheClientTests(ITestOutputHelper output) : base(output) {
            FlushAll();
        }

        protected override ICacheClient GetCacheClient() {
            return new RedisHybridCacheClient(SharedConnection.GetMuxer(), loggerFactory: Log);
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

        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error flushing redis");
                }
            }
        }
    }
}
