using System;
using System.Collections.Generic;
using System.Threading;
using Foundatio.Caching;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching {
    public abstract class CacheClientTestsBase : CaptureTests {
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

        public virtual void CanUseScopedCaches() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
                var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");
                var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

                cache.Set("test", 1);
                scopedCache1.Set("test", 2);
                nestedScopedCache1.Set("test", 3);

                Assert.Equal(1, cache.Get<int>("test"));
                Assert.Equal(2, scopedCache1.Get<int>("test"));
                Assert.Equal(3, nestedScopedCache1.Get<int>("test"));

                Assert.Equal(3, scopedCache1.Get<int>("nested:test"));
                Assert.Equal(3, cache.Get<int>("scoped1:nested:test"));

                scopedCache2.Set("test", 1);

                scopedCache1.FlushAll();
                Assert.Null(scopedCache1.Get<int?>("test"));
                Assert.Null(nestedScopedCache1.Get<int?>("test"));
                Assert.Equal(1, cache.Get<int>("test"));
                Assert.Equal(1, scopedCache2.Get<int>("test"));

                scopedCache2.FlushAll();
                Assert.Null(scopedCache1.Get<int?>("test"));
                Assert.Null(nestedScopedCache1.Get<int?>("test"));
                Assert.Null(scopedCache2.Get<int?>("test"));
                Assert.Equal(1, cache.Get<int>("test"));
            }
        }


        public virtual void CanRemoveByPrefix()
        {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                cache.FlushAll();
                
                string prefix = "blah:";
                cache.Set("test", 1);
                cache.Set(prefix + "test", 1);
                cache.Set(prefix + "test2", 4);
                Assert.Equal(1, cache.Get<int>(prefix + "test"));
                Assert.Equal(1, cache.Get<int>("test"));

                cache.RemoveByPrefix(prefix);
                Assert.Null(cache.Get<int?>(prefix + "test"));
                Assert.Null(cache.Get<int?>(prefix + "test2"));
                Assert.Equal(1, cache.Get<int>("test"));
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
                value.Type = "modified";
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
                success = cache.Set("test2", 1, expiresAt.AddMilliseconds(100));
                Assert.True(success);
                Assert.Equal(1, cache.Get<int?>("test"));
                Assert.True(cache.GetExpiration("test").Value.Subtract(expiresAt) < TimeSpan.FromSeconds(1));

                Thread.Sleep(500);
                Assert.Null(cache.Get<int?>("test"));
                Assert.Null(cache.GetExpiration("test"));
                Assert.Null(cache.Get<int?>("test2"));
                Assert.Null(cache.GetExpiration("test2"));
            }
        }

        public virtual void MeasureThroughput()
        {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            cacheClient.FlushAll();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++)
            {
                cacheClient.Set("test", 13422);
                cacheClient.Set("flag", true);
                Assert.Equal(13422, cacheClient.Get<int>("test"));
                Assert.Null(cacheClient.Get<int?>("test2"));
                Assert.True(cacheClient.Get<bool>("flag"));
                metrics.Counter("work");
            }
            metrics.DisplayStats(_writer);
        }

        public virtual void MeasureSerializerSimpleThroughput()
        {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            cacheClient.FlushAll();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++)
            {
                cacheClient.Set("test", new SimpleModel
                {
                    Data1 = "Hello",
                    Data2 = 12
                });
                var model = cacheClient.Get<SimpleModel>("test");
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                metrics.Counter("work");
            }
            metrics.DisplayStats(_writer);
        }

        public virtual void MeasureSerializerComplexThroughput()
        {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            cacheClient.FlushAll();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++)
            {
                cacheClient.Set("test", new ComplexModel
                {
                    Data1 = "Hello",
                    Data2 = 12,
                    Data3 = true,
                    Simple = new SimpleModel { Data1 = "hi", Data2 = 13 },
                    Simples = new List<SimpleModel> { new SimpleModel { Data1 = "hey", Data2 = 45 }, new SimpleModel { Data1 = "next", Data2 = 3423 } },
                    DictionarySimples = new Dictionary<string, SimpleModel> { { "sdf", new SimpleModel { Data1 = "Sachin" } } }
                });
                var model = cacheClient.Get<ComplexModel>("test");
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                metrics.Counter("work");
            }
            metrics.DisplayStats(_writer);
        }

        protected CacheClientTestsBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }
    }

    public class SimpleModel
    {
        public string Data1 { get; set; }
        public int Data2 { get; set; }
    }

    public class ComplexModel
    {
        public string Data1 { get; set; }
        public int Data2 { get; set; }
        public SimpleModel Simple { get; set; }
        public ICollection<SimpleModel> Simples { get; set; }
        public bool Data3 { get; set; }
        public IDictionary<string, SimpleModel> DictionarySimples { get; set; }
    }

    public class MyData
    {
        public string Type { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Message { get; set; }
    }
}