using System;
using System.Diagnostics;
using System.Threading;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching {
    public class InMemoryCacheClientTests : CacheClientTestsBase {
        protected override ICacheClient GetCacheClient() {
            return new InMemoryCacheClient();
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
        public void CanSetMaxItems() {
            // run in tight loop so that the code is warmed up and we can catch timing issues
            for (int x = 0; x < 5; x++) {
                var cache = GetCacheClient() as InMemoryCacheClient;
                if (cache == null)
                    return;

                cache.FlushAll();

                cache.MaxItems = 10;
                for (int i = 0; i < cache.MaxItems; i++)
                    cache.Set("test" + i, i);

                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                cache.Set("next", 1);
                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                Assert.Null(cache.Get<int?>("test0"));
                Thread.Sleep(50); // keep the last access ticks from being the same for all items
                Assert.NotNull(cache.Get<int?>("test1"));
                cache.Set("next2", 2);
                Trace.WriteLine(String.Join(",", cache.Keys));
                Assert.Null(cache.Get<int?>("test2"));
                Assert.NotNull(cache.Get<int?>("test1"));
            }
        }
    }
}