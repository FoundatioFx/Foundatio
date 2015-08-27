using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public abstract class CacheClientTestsBase : CaptureTests {
        protected virtual ICacheClient GetCacheClient() {
            return null;
        }

        public virtual async Task CanSetAndGetValue() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                await cache.SetAsync("test", 1);
                var value = await cache.GetAsync<int>("test");
                Assert.Equal(1, value);
            }
        }

        public virtual async Task CanUseScopedCaches() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
                var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");
                var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

                await cache.SetAsync("test", 1);
                await scopedCache1.SetAsync("test", 2);
                await nestedScopedCache1.SetAsync("test", 3);

                Assert.Equal(1, await cache.GetAsync<int>("test"));
                Assert.Equal(2, await scopedCache1.GetAsync<int>("test"));
                Assert.Equal(3, await nestedScopedCache1.GetAsync<int>("test"));

                Assert.Equal(3, await scopedCache1.GetAsync<int>("nested:test"));
                Assert.Equal(3, await cache.GetAsync<int>("scoped1:nested:test"));

                await scopedCache2.SetAsync("test", 1);

                await scopedCache1.RemoveAllAsync();
                Assert.Null(await scopedCache1.GetAsync<int?>("test"));
                Assert.Null(await nestedScopedCache1.GetAsync<int?>("test"));
                Assert.Equal(1, await cache.GetAsync<int>("test"));
                Assert.Equal(1, await scopedCache2.GetAsync<int>("test"));

                await scopedCache2.RemoveAllAsync();
                Assert.Null(await scopedCache1.GetAsync<int?>("test"));
                Assert.Null(await nestedScopedCache1.GetAsync<int?>("test"));
                Assert.Null(await scopedCache2.GetAsync<int?>("test"));
                Assert.Equal(1, await cache.GetAsync<int>("test"));
            }
        }


        public virtual async Task CanRemoveByPrefix() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();
                
                string prefix = "blah:";
                await cache.SetAsync("test", 1);
                await cache.SetAsync(prefix + "test", 1);
                await cache.SetAsync(prefix + "test2", 4);
                Assert.Equal(1, await cache.GetAsync<int>(prefix + "test"));
                Assert.Equal(1, await cache.GetAsync<int>("test"));

                await cache.RemoveByPrefixAsync(prefix);
                Assert.Null(await cache.GetAsync<int?>(prefix + "test"));
                Assert.Null(await cache.GetAsync<int?>(prefix + "test2"));
                Assert.Equal(1, await cache.GetAsync<int>("test"));
            }
        }

        public virtual async Task CanSetAndGetObject() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var dt = DateTimeOffset.Now;
                var value = new MyData {Type = "test", Date = dt, Message = "Hello World"};
                await cache.SetAsync("test", value);
                value.Type = "modified";
                var cachedValue = await cache.GetAsync<MyData>("test");
                Assert.NotNull(cachedValue);
                Assert.Equal(dt, cachedValue.Date);
                Assert.False(value.Equals(cachedValue), "Should not be same reference object.");
                Assert.Equal("Hello World", cachedValue.Message);
                Assert.Equal("test", cachedValue.Type);
            }
        }

        public virtual async Task CanSetExpiration() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
                var success = await cache.SetAsync("test", 1, expiresAt);
                Assert.True(success);
                success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(100));
                Assert.True(success);
                Assert.Equal(1, await cache.GetAsync<int?>("test"));
                Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

                Thread.Sleep(500);
                Assert.Null(await cache.GetAsync<int?>("test"));
                Assert.Null(await cache.GetExpirationAsync("test"));
                Assert.Null(await cache.GetAsync<int?>("test2"));
                Assert.Null(await cache.GetExpirationAsync("test2"));
            }
        }

        protected CacheClientTestsBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }
    }

    public class MyData
    {
        public string Type { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Message { get; set; }
    }
}