using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public class InMemoryCacheClientTests : CacheClientTestsBase {
        public InMemoryCacheClientTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ICacheClient GetCacheClient() {
            return new InMemoryCacheClient();
        }

        [Fact]
        public override Task CanSetAndGetValue() {
            return base.CanSetAndGetValue();
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
        public async Task CanSetMaxItems() {
            // run in tight loop so that the code is warmed up and we can catch timing issues
            for (int x = 0; x < 5; x++) {
                var cache = GetCacheClient() as InMemoryCacheClient;
                if (cache == null)
                    return;

                await cache.RemoveAllAsync().AnyContext();

                cache.MaxItems = 10;
                for (int i = 0; i < cache.MaxItems; i++)
                    await cache.SetAsync("test" + i, i).AnyContext();

                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                await cache.SetAsync("next", 1).AnyContext();
                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                Assert.Null(await cache.GetAsync<int?>("test0").AnyContext());
                Assert.Equal(1, cache.Misses);
                await Task.Delay(50).AnyContext(); // keep the last access ticks from being the same for all items
                Assert.NotNull(await cache.GetAsync<int?>("test1").AnyContext());
                Assert.Equal(1, cache.Hits);
                await cache.SetAsync("next2", 2).AnyContext();
                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Null(await cache.GetAsync<int?>("test2").AnyContext());
                Assert.Equal(2, cache.Misses);
                Assert.NotNull(await cache.GetAsync<int?>("test1").AnyContext());
                Assert.Equal(2, cache.Misses);
            }
        }
    }
}