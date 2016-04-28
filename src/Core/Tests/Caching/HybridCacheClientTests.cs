using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public class HybridCacheClientTests: CacheClientTestsBase {
        private readonly ICacheClient _distributedCache = new InMemoryCacheClient();
        private readonly IMessageBus _messageBus = new InMemoryMessageBus();

        public HybridCacheClientTests(ITestOutputHelper output) : base(output) {}

        protected override ICacheClient GetCacheClient() {
            return new HybridCacheClient(_distributedCache, _messageBus, Log);
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
        public override Task CanIncrementAndExpire() {
            return base.CanIncrementAndExpire();
        }

        [Fact]
        public virtual async Task WillUseLocalCache() {
            var firstCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(firstCache);

            var secondCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(secondCache);

            await firstCache.SetAsync("first1", 1);
            await firstCache.IncrementAsync("first2");
            // doesnt use localcache for simple types
            Assert.Equal(0, firstCache.LocalCache.Count);

            var cacheKey = Guid.NewGuid().ToString("N").Substring(10);
            await firstCache.SetAsync(cacheKey, new SimpleModel { Data1 = "test" });
            Assert.Equal(1, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.Equal(0, firstCache.LocalCacheHits);

            Assert.True((await firstCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
            Assert.Equal(1, firstCache.LocalCacheHits);

            Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
            Assert.Equal(0, secondCache.LocalCacheHits);
            Assert.Equal(1, secondCache.LocalCache.Count);

            Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
            Assert.Equal(1, secondCache.LocalCacheHits);
        }

        [Fact]
        public virtual async Task WillExpireRemoteItems() {
            Log.MinimumLevel = LogLevel.Trace;

            _logger.Trace("Warm the log...");
            var firstCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(firstCache);

            var secondCache = GetCacheClient() as HybridCacheClient;
            Assert.NotNull(secondCache);

            var countdownEvent = new AsyncCountdownEvent(2);
            firstCache.LocalCache.ItemExpired.AddSyncHandler((sender, args) => {
                _logger.Trace("First expired: {0}", args.Key);
                countdownEvent.Signal();
            });
            secondCache.LocalCache.ItemExpired.AddSyncHandler((sender, args) => {
                _logger.Trace("Second expired: {0}", args.Key);
                countdownEvent.Signal();
            });

            var cacheKey = "willexpireremote";
            _logger.Trace("First Set");
            await firstCache.SetAsync(cacheKey, new SimpleModel { Data1 = "test" }, TimeSpan.FromMilliseconds(250));
            _logger.Trace("Done First Set");
            Assert.Equal(1, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            Assert.Equal(0, firstCache.LocalCacheHits);

            _logger.Trace("First Get");
            Assert.True((await firstCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
            Assert.Equal(1, firstCache.LocalCacheHits);

            await Task.Delay(50);

            _logger.Trace("Second Get");
            Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
            Assert.Equal(0, secondCache.LocalCacheHits);
            Assert.Equal(1, secondCache.LocalCache.Count);

            _logger.Trace("Second Get from local cache");
            Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
            Assert.Equal(1, secondCache.LocalCacheHits);

            var sw = Stopwatch.StartNew();
            await countdownEvent.WaitAsync(new CancellationTokenSource(500).Token);
            sw.Stop();
            Trace.WriteLine(sw.Elapsed);
            Assert.Equal(0, firstCache.LocalCache.Count);
            Assert.Equal(0, secondCache.LocalCache.Count);
            //Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 200);
        }
    }
}
