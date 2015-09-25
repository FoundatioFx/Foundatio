using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
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
                await cache.RemoveAllAsync();

                await cache.SetAsync("test", 1);
                Assert.Equal(1, await cache.GetAsync<int>("test"));

                Assert.False(await cache.AddAsync("test", 2));
                Assert.Equal(1, await cache.GetAsync<int>("test"));

                Assert.True(await cache.ReplaceAsync("test", 2));
                Assert.Equal(2, await cache.GetAsync<int>("test"));

                Assert.True(await cache.RemoveAsync("test"));
                Assert.Null(await cache.GetAsync<int?>("test"));
                
                Assert.True(await cache.AddAsync("test", 2));
                Assert.Equal(2, await cache.GetAsync<int>("test"));
                
                Assert.True(await cache.ReplaceAsync("test", new MyData { Message = "Testing" }));
                var result = await cache.TryGetAsync<MyData>("test");
                Assert.NotNull(result);
                Assert.True(result.HasValue);
                Assert.Equal("Testing", result.Value.Message);
            }
        }
        
        public virtual async Task CanAddConncurrently() {
            var cache = GetCacheClient();
            if (cache == null)
                return;
            
            using (cache) {
                await cache.RemoveAllAsync();

                long adds = 0;
                await Run.InParallel(5, async i => {
                    if (await cache.AddAsync("test", i))
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
                await cache.RemoveAllAsync();

                await cache.SetAsync<int>("test", 1);
                var cacheValue = await cache.GetAsync<long?>("test");
                Assert.True(cacheValue.HasValue);
                Assert.Equal(1L, cacheValue.Value);

                await cache.SetAsync<long>("test", Int64.MaxValue);
                cacheValue = await cache.GetAsync<int?>("test");
                Assert.False(cacheValue.HasValue);

                cacheValue = await cache.GetAsync<long?>("test");
                Assert.True(cacheValue.HasValue);
                Assert.Equal(Int64.MaxValue, cacheValue.Value);

                await cache.SetAsync<MyData>("test", new MyData {
                    Message = "test"
                });
                cacheValue = await cache.GetAsync<long?>("test");
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

                await cache.SetAsync("test", 1);
                await scopedCache1.SetAsync("test", 2);
                await nestedScopedCache1.SetAsync("test", 3);

                Assert.Equal(1, await cache.GetAsync<int>("test"));
                Assert.Equal(2, await scopedCache1.GetAsync<int>("test"));
                Assert.Equal(3, await nestedScopedCache1.GetAsync<int>("test"));

                Assert.Equal(3, await scopedCache1.GetAsync<int>("nested:test"));
                Assert.Equal(3, await cache.GetAsync<int>("scoped1:nested:test"));

                await scopedCache2.SetAsync("test", 1);

                var result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
                Assert.Equal(2, result);

                // delete without any matching keys
                result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
                Assert.Equal(0, result);

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
                var value = new MyData {
                    Type = "test",
                    Date = dt,
                    Message = "Hello World"
                };
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

                await Task.Delay(500);
                Assert.Null(await cache.GetAsync<int?>("test"));
                Assert.Null(await cache.GetExpirationAsync("test"));
                Assert.Null(await cache.GetAsync<int?>("test2"));
                Assert.Null(await cache.GetExpirationAsync("test2"));
            }
        }

        public virtual async Task MeasureThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", 13422);
                await cacheClient.SetAsync("flag", true);
                Assert.Equal(13422, await cacheClient.GetAsync<int>("test"));
                Assert.Null(await cacheClient.GetAsync<int?>("test2"));
                Assert.True(await cacheClient.GetAsync<bool>("flag"));
                await metrics.CounterAsync("work");
            }
            metrics.DisplayStats(_writer);
        }

        public virtual async Task MeasureSerializerSimpleThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync();

            const int itemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            for (int i = 0; i < itemCount; i++) {
                await cacheClient.SetAsync("test", new SimpleModel {
                    Data1 = "Hello",
                    Data2 = 12
                });
                var model = await cacheClient.GetAsync<SimpleModel>("test");
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                await metrics.CounterAsync("work");
            }

            metrics.DisplayStats(_writer);
        }

        public virtual async Task MeasureSerializerComplexThroughput() {
            var cacheClient = GetCacheClient();
            if (cacheClient == null)
                return;

            await cacheClient.RemoveAllAsync();

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
                        { "sdf", new SimpleModel { Data1 = "Sachin" } }
                    },

                    DerivedDictionarySimples = new SampleDictionary<string, SimpleModel> {
                        { "sdf", new SimpleModel { Data1 = "Sachin" } }
                    }
                });

                var model = await cacheClient.GetAsync<ComplexModel>("test");
                Assert.NotNull(model);
                Assert.Equal("Hello", model.Data1);
                Assert.Equal(12, model.Data2);
                await metrics.CounterAsync("work");
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
        public SampleDictionary<string, SimpleModel> DerivedDictionarySimples { get; set; } 
    }

    public class MyData {
        private readonly string _blah = "blah";
        public string Blah => _blah;
        public string Type { get; set; }
        public DateTimeOffset Date { get; set; }
        public string Message { get; set; }
    }

    public class SampleDictionary<TKey, TValue> : IDictionary<TKey, TValue> {
        private readonly IDictionary<TKey, TValue> _dictionary;

        public SampleDictionary() {
            _dictionary = new Dictionary<TKey, TValue>();
        }

        public SampleDictionary(IDictionary<TKey, TValue> dictionary) {
            _dictionary = new Dictionary<TKey, TValue>(dictionary);
        }

        public SampleDictionary(IEqualityComparer<TKey> comparer) {
            _dictionary = new Dictionary<TKey, TValue>(comparer);
        }

        public SampleDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer) {
            _dictionary = new Dictionary<TKey, TValue>(dictionary, comparer);
        }

        public void Add(TKey key, TValue value) {
            _dictionary.Add(key, value);
        }

        public void Add(KeyValuePair<TKey, TValue> item) {
            _dictionary.Add(item);
        }

        public bool Remove(TKey key) {
            return _dictionary.Remove(key);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item) {
            return _dictionary.Remove(item);
        }

        public void Clear() {
            _dictionary.Clear();
        }

        public bool ContainsKey(TKey key) {
            return _dictionary.ContainsKey(key);
        }

        public bool Contains(KeyValuePair<TKey, TValue> item) {
            return _dictionary.Contains(item);
        }

        public bool TryGetValue(TKey key, out TValue value) {
            return _dictionary.TryGetValue(key, out value);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) {
            _dictionary.CopyTo(array, arrayIndex);
        }

        public ICollection<TKey> Keys => _dictionary.Keys;

        public ICollection<TValue> Values => _dictionary.Values;

        public int Count => _dictionary.Count;

        public bool IsReadOnly => _dictionary.IsReadOnly;

        public TValue this[TKey key] {
            get { return _dictionary[key]; }
            set { _dictionary[key] = value; }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
            return _dictionary.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }
    }
}