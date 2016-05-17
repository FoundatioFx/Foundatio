using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public class InMemoryCacheClientTests : CacheClientTestsBase {
        public InMemoryCacheClientTests(ITestOutputHelper output) : base(output) {}

        protected override ICacheClient GetCacheClient() {
            return new InMemoryCacheClient(Log);
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
        public async Task CanSetMaxItems() {
            // run in tight loop so that the code is warmed up and we can catch timing issues
            for (int x = 0; x < 5; x++) {
                var cache = GetCacheClient() as InMemoryCacheClient;
                if (cache == null)
                    return;

                await cache.RemoveAllAsync();

                cache.MaxItems = 10;
                for (int i = 0; i < cache.MaxItems; i++)
                    await cache.SetAsync("test" + i, i);

                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                await cache.SetAsync("next", 1);
                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                Assert.False((await cache.GetAsync<int>("test0")).HasValue);
                Assert.Equal(1, cache.Misses);
                await Task.Delay(50); // keep the last access ticks from being the same for all items
                Assert.NotNull(await cache.GetAsync<int?>("test1"));
                Assert.Equal(1, cache.Hits);
                await cache.SetAsync("next2", 2);
                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.False((await cache.GetAsync<int>("test2")).HasValue);
                Assert.Equal(2, cache.Misses);
                Assert.True((await cache.GetAsync<int>("test1")).HasValue);
                Assert.Equal(2, cache.Misses);
            }
        }
    }
}