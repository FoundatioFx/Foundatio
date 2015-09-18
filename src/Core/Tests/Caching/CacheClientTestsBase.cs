﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

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
                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());

                Assert.False(await cache.AddAsync("test", 2));
                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());

                Assert.True(await cache.ReplaceAsync("test", 2));
                Assert.Equal(2, await cache.GetAsync<int>("test").AnyContext());

                Assert.True(await cache.RemoveAsync("test"));
                Assert.Null(await cache.GetAsync<int?>("test").AnyContext());
                
                Assert.True(await cache.AddAsync("test", 2));
                Assert.Equal(2, await cache.GetAsync<int>("test").AnyContext());
            }
        }
        
        public virtual async Task CanAddConncurrently() {
            var cache = GetCacheClient();
            if (cache == null)
                return;
            
            using (cache) {
                await cache.RemoveAllAsync().AnyContext();

                long adds = 0;
                await Run.InParallel(5, async i => {
                    if (await cache.AddAsync("test", i).AnyContext())
                        Interlocked.Increment(ref adds);
                });

                Assert.Equal(1, adds);
            }
        }

        public virtual async Task CanTryGet() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync().AnyContext();

                await cache.SetAsync<int>("test", 1).AnyContext();
                var cacheValue = await cache.GetAsync<long?>("test").AnyContext();
                Assert.True(cacheValue.HasValue);
                Assert.Equal(1L, cacheValue.Value);

                await cache.SetAsync<long>("test", Int64.MaxValue).AnyContext();
                cacheValue = await cache.GetAsync<int?>("test").AnyContext();
                Assert.False(cacheValue.HasValue);

                cacheValue = await cache.GetAsync<long?>("test").AnyContext();
                Assert.True(cacheValue.HasValue);
                Assert.Equal(Int64.MaxValue, cacheValue.Value);

                await cache.SetAsync<MyData>("test", new MyData {
                    Message = "test"
                }).AnyContext();
                cacheValue = await cache.GetAsync<long?>("test").AnyContext();
                Assert.False(cacheValue.HasValue);
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

                Assert.Equal(1, await cache.GetAsync<int>("test").AnyContext());
                Assert.Equal(2, await scopedCache1.GetAsync<int>("test").AnyContext());
                Assert.Equal(3, await nestedScopedCache1.GetAsync<int>("test").AnyContext());

                Assert.Equal(3, await scopedCache1.GetAsync<int>("nested:test").AnyContext());
                Assert.Equal(3, await cache.GetAsync<int>("scoped1:nested:test").AnyContext());

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
                var value = new MyData {
                    Type = "test",
                    Date = dt,
                    Message = "Hello World"
                };
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

                await Task.Delay(500).AnyContext();
                Assert.Null(await cache.GetAsync<int?>("test").AnyContext());
                Assert.Null(await cache.GetExpirationAsync("test").AnyContext());
                Assert.Null(await cache.GetAsync<int?>("test2").AnyContext());
                Assert.Null(await cache.GetExpirationAsync("test2").AnyContext());
            }
        }

        public virtual async Task MeasureThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync().AnyContext();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", 13422).AnyContext();
                await cacheClient.SetAsync("flag", true).AnyContext();
                Assert.Equal(13422, await cacheClient.GetAsync<int>("test").AnyContext());
                Assert.Null(await cacheClient.GetAsync<int?>("test2").AnyContext());
                Assert.True(await cacheClient.GetAsync<bool>("flag").AnyContext());
                await metrics.CounterAsync("work").AnyContext();
            }
            metrics.DisplayStats(_writer);
        }

        public virtual async Task MeasureSerializerSimpleThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync().AnyContext();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", new SimpleModel {
                    Data1 = "Hello",
                    Data2 = 12
                }).AnyContext();
                var model = await cacheClient.GetAsync<SimpleModel>("test").AnyContext();
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                await metrics.CounterAsync("work").AnyContext();
            }

            metrics.DisplayStats(_writer);
        }

        public virtual async Task MeasureSerializerComplexThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync().AnyContext();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", new ComplexModel {
                    Data1 = "Hello",
                    Data2 = 12,
                    Data3 = true,
                    Simple = new SimpleModel {
                        Data1 = "hi",
                        Data2 = 13
                    },
                    Simples = new List<SimpleModel> {
                        new SimpleModel {
                            Data1 = "hey",
                            Data2 = 45
                        },
                        new SimpleModel {
                            Data1 = "next",
                            Data2 = 3423
                        }
                    },
                    DictionarySimples = new Dictionary<string, SimpleModel> {
                        { "sdf", new SimpleModel {
                            Data1 = "Sachin"
                        } }
                    }
                }).AnyContext();

                var model = await cacheClient.GetAsync<ComplexModel>("test").AnyContext();
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                await metrics.CounterAsync("work").AnyContext();
            }

            metrics.DisplayStats(_writer);
        }

        protected CacheClientTestsBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}
    }

    public class SimpleModel {
        public string Data1 { get; set; }
        public int Data2 { get; set; }
    }

    public class ComplexModel {
        public string Data1 { get; set; }
        public int Data2 { get; set; }
        public SimpleModel Simple { get; set; }
        public ICollection<SimpleModel> Simples { get; set; }
        public bool Data3 { get; set; }
        public IDictionary<string, SimpleModel> DictionarySimples { get; set; }
    }

    public class MyData {
        public string Type { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Message { get; set; }
    }
}