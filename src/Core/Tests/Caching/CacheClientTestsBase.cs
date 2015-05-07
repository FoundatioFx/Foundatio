using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching {
    public abstract class CacheClientTestsBase {
        protected virtual ICacheClient GetCacheClient() {
            return null;
        }

        public virtual void CanSetAndGetValue() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                cache.FlushAll();

                cache.Set("test", 1);
                var value = cache.Get<int>("test");
                Assert.Equal(1, value);
            }
        }

        public virtual void CanSetAndGetObject() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                cache.FlushAll();

                var dt = DateTimeOffset.Now;
                var value = new MyData {Type = "test", Date = dt, Message = "Hello World"};
                cache.Set("test", value);
                var cachedValue = cache.Get<MyData>("test");
                Assert.NotNull(cachedValue);
                Assert.Equal(dt, cachedValue.Date);
                Assert.False(value.Equals(cachedValue), "Should not be same reference object.");
                Assert.Equal("Hello World", cachedValue.Message);
                Assert.Equal("test", cachedValue.Type);
            }
        }

        public virtual void CanSetExpiration() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                cache.FlushAll();

                var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
                var success = cache.Set("test", 1, expiresAt);
                Assert.True(success);
                Assert.Equal(1, cache.Get<int>("test"));
                Assert.True(cache.GetExpiration("test").Value.Subtract(expiresAt) < TimeSpan.FromSeconds(1));

                Task.Delay(TimeSpan.FromMilliseconds(500)).Wait();
                Assert.Equal(0, cache.Get<int>("test"));
                Assert.Null(cache.GetExpiration("test"));
            }
        }
    }

    public class MyData
    {
        public string Type { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Message { get; set; }
    }
}