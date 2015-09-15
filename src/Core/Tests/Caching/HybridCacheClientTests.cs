using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Messaging;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public class HybridCacheClientTests: CacheClientTestsBase {
        private readonly ICacheClient _distributedCache = new InMemoryCacheClient();
        private readonly IMessageBus _messageBus = new InMemoryMessageBus();

        public HybridCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ICacheClient GetCacheClient() {
            return new HybridCacheClient(_distributedCache, _messageBus);
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
        public override Task CanTryGet() {
            return base.CanTryGet();
        }

        [Fact]
        public override Task CanUseScopedCaches() {
            return base.CanUseScopedCaches();
        }
        
        [Fact]
        public override Task CanSetAndGetObject() {
            return base.CanSetAndGetObject();
        }

        [Fact]
        public override Task CanRemoveByPrefix() {
            return base.CanRemoveByPrefix();
        }

        [Fact]
        public override Task CanSetExpiration() {
            return base.CanSetExpiration();
        }

        [Fact]
        public virtual async Task WillUseLocalCache() {
            var firstCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(firstCache);

            var secondCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(secondCache);

            await firstCache.SetAsync("test", 1).AnyContext();
            Assert.Equal(1, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.Equal(0, firstCache.LocalCacheHits);

            Assert.Equal(1, await firstCache.GetAsync<int>("test").AnyContext());
            Assert.Equal(1, firstCache.LocalCacheHits);

            Assert.Equal(1, await secondCache.GetAsync<int>("test").AnyContext());
            Assert.Equal(0, secondCache.LocalCacheHits);
            Assert.Equal(1, secondCache.LocalCache.Count);

            Assert.Equal(1, await secondCache.GetAsync<int>("test").AnyContext());
            Assert.Equal(1, secondCache.LocalCacheHits);
        }

        [Fact]
        public virtual async Task WillExpireRemoteItems() {
            Logger.Trace().Message("Warm the log...").Write();
            var firstCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(firstCache);

            var secondCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(secondCache);

            await firstCache.SetAsync("test", 1, TimeSpan.FromMilliseconds(100)).AnyContext();
            Assert.Equal(1, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.Equal(0, firstCache.LocalCacheHits);

            Assert.Equal(1, await firstCache.GetAsync<int>("test").AnyContext());
            Assert.Equal(1, firstCache.LocalCacheHits);

            Assert.Equal(1, await secondCache.GetAsync<int>("test").AnyContext());
            Assert.Equal(0, secondCache.LocalCacheHits);
            Assert.Equal(1, secondCache.LocalCache.Count);

            Assert.Equal(1, await secondCache.GetAsync<int>("test").AnyContext());
            Assert.Equal(1, secondCache.LocalCacheHits);

            var sw = Stopwatch.StartNew();
            while ((firstCache.LocalCache.Count > 0 || secondCache.LocalCache.Count > 0) && sw.ElapsedMilliseconds < 150)
                await Task.Delay(25).AnyContext();

            sw.Stop();
            Trace.WriteLine(sw.Elapsed);
            Assert.Equal(0, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 200);
        }
    }
}
