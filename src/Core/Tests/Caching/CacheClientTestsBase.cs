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
                await cache.RemoveAllAsync().AnyContext();

                await cache.SetAsync("test", 1).AnyContext();
                var value = await cache.GetAsync<int>("test").AnyContext();
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

                await cache.SetAsync("test", 1).AnyContext();
                await scopedCache1.SetAsync("test", 2).AnyContext();
                await nestedScopedCache1.SetAsync("test", 3).AnyContext();

                Assert.Equal(1, await cache.GetAsync<int>("test")).AnyContext();
                Assert.Equal(2, await scopedCache1.GetAsync<int>("test")).AnyContext();
                Assert.Equal(3, await nestedScopedCache1.GetAsync<int>("test")).AnyContext();

                Assert.Equal(3, await scopedCache1.GetAsync<int>("nested:test")).AnyContext();
                Assert.Equal(3, await cache.GetAsync<int>("scoped1:nested:test")).AnyContext();

                await scopedCache2.SetAsync("test", 1).AnyContext();

                await scopedCache1.RemoveAllAsync().AnyContext();
                Assert.Null(await scopedCache1.GetAsync<int?>("test").AnyContext());
                Assert.Null(await nestedScopedCache1.GetAsync<int?>("test").AnyContext());
                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());
                Assert.Equal(1, await scopedCache2.GetAsync<int>("test").AnyContext());

                await scopedCache2.RemoveAllAsync().AnyContext();
                Assert.Null(await scopedCache1.GetAsync<int?>("test").AnyContext());
                Assert.Null(await nestedScopedCache1.GetAsync<int?>("test").AnyContext());
                Assert.Null(await scopedCache2.GetAsync<int?>("test").AnyContext());
                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());
            }
        }


        public virtual async Task CanRemoveByPrefix() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync().AnyContext();
                
                string prefix = "blah:";
                await cache.SetAsync("test", 1).AnyContext();
                await cache.SetAsync(prefix + "test", 1).AnyContext();
                await cache.SetAsync(prefix + "test2", 4).AnyContext();
                Assert.Equal(1, await cache.GetAsync<int>(prefix + "test").AnyContext());
                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());

                await cache.RemoveByPrefixAsync(prefix).AnyContext();
                Assert.Null(await cache.GetAsync<int?>(prefix + "test").AnyContext());
                Assert.Null(await cache.GetAsync<int?>(prefix + "test2").AnyContext());
                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());
            }
        }

        public virtual async Task CanSetAndGetObject() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync().AnyContext();

                var dt = DateTimeOffset.Now;
                var value = new MyData {Type = "test", Date = dt, Message = "Hello World"};
                await cache.SetAsync("test", value).AnyContext();
                value.Type = "modified";
                var cachedValue = await cache.GetAsync<MyData>("test").AnyContext();
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
                await cache.RemoveAllAsync().AnyContext();

                var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
                var success = await cache.SetAsync("test", 1, expiresAt).AnyContext();
                Assert.True(success);
                success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(100)).AnyContext();
                Assert.True(success);
                Assert.Equal(1, await cache.GetAsync<int?>("test").AnyContext());
                Assert.True((await cache.GetExpirationAsync("test").AnyContext()).Value < TimeSpan.FromSeconds(1));

                Thread.Sleep(500);
                Assert.Null(await cache.GetAsync<int?>("test").AnyContext());
                Assert.Null(await cache.GetExpirationAsync("test").AnyContext());
                Assert.Null(await cache.GetAsync<int?>("test2").AnyContext());
                Assert.Null(await cache.GetExpirationAsync("test2").AnyContext());
            }
        }

        protected CacheClientTestsBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}
    }

    public class MyData {
        public string Type { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Message { get; set; }
    }
}