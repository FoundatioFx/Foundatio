using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Tests.Extensions;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public class HybridCacheClientTests: CacheClientTestsBase, IDisposable {
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
        public override Task CanManageSets() {
            return base.CanManageSets();
        }

        [Fact]
        public virtual async Task WillUseLocalCache() {
            using (var firstCache = GetCacheClient() as HybridCacheClient) {
                Assert.NotNull(firstCache);

                using (var secondCache = GetCacheClient() as HybridCacheClient) {
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
                    Assert.Equal(1, firstCache.LocalCache.Count);

                    Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
                    Assert.Equal(0, secondCache.LocalCacheHits);
                    Assert.Equal(1, secondCache.LocalCache.Count);

                    Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
                    Assert.Equal(1, secondCache.LocalCacheHits);
                }
            }
        }

        [Fact]
        public virtual async Task WillExpireRemoteItems() {
            var countdownEvent = new AsyncCountdownEvent(2);

            using (var firstCache = GetCacheClient() as HybridCacheClient) {
                Assert.NotNull(firstCache);
                Action<object, ItemExpiredEventArgs> expiredHandler = (sender, args) => {
                    _logger.Trace("First expired: {0}", args.Key);
                    countdownEvent.Signal();
                };

                using (firstCache.LocalCache.ItemExpired.AddSyncHandler(expiredHandler)) {
                    using (var secondCache = GetCacheClient() as HybridCacheClient) {
                        Assert.NotNull(secondCache);
                        Action<object, ItemExpiredEventArgs> expiredHandler2 = (sender, args) => {
                            _logger.Trace("Second expired: {0}", args.Key);
                            countdownEvent.Signal();
                        };

                        using (secondCache.LocalCache.ItemExpired.AddSyncHandler(expiredHandler2)) {
                            string cacheKey = "willexpireremote";
                            _logger.Trace("First Set");
                            Assert.True(await firstCache.AddAsync(cacheKey, new SimpleModel { Data1 = "test" }, TimeSpan.FromMilliseconds(150)));
                            _logger.Trace("Done First Set");
                            Assert.Equal(1, firstCache.LocalCache.Count);

                            _logger.Trace("Second Get");
                            Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
                            _logger.Trace("Done Second Get");
                            Assert.Equal(1, secondCache.LocalCache.Count);

                            var sw = Stopwatch.StartNew();
                            await countdownEvent.WaitAsync(TimeSpan.FromMilliseconds(250));
                            sw.Stop();

                            _logger.Trace("Time {0}", sw.Elapsed);
                            Assert.Equal(0, countdownEvent.CurrentCount);
                            Assert.Equal(0, firstCache.LocalCache.Count);
                            Assert.Equal(0, secondCache.LocalCache.Count);
                        }
                    }
                }
            }
        }

        public void Dispose() {
            _distributedCache.Dispose();
            _messageBus.Dispose();
        }
    }
}
