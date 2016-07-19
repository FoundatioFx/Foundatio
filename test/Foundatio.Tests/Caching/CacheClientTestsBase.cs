using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Utility;
using Newtonsoft.Json;
using System.Linq;
using Foundatio.Extensions;

namespace Foundatio.Tests.Caching {
    public abstract class CacheClientTestsBase : TestWithLoggingBase {
        protected CacheClientTestsBase(ITestOutputHelper output) : base(output) {
            SystemClock.Reset();
        }

        protected virtual ICacheClient GetCacheClient() {
            return null;
        }

        public virtual async Task CanGetAll() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                await cache.SetAsync("test1", 1);
                await cache.SetAsync("test2", 2);
                await cache.SetAsync("test3", 3);
                var result = await cache.GetAllAsync<int>(new [] { "test1", "test2", "test3" });
                Assert.NotNull(result);
                Assert.Equal(3, result.Count);

                await cache.SetAsync("obj1", new SimpleModel {Data1 = "data 1", Data2 = 1 });
                await cache.SetAsync("obj2", new SimpleModel { Data1 = "data 2", Data2 = 2 });
                await cache.SetAsync("obj3", (SimpleModel)null);

                var json = JsonConvert.SerializeObject(new SimpleModel {Data1 = "test 1", Data2 = 4});
                await cache.SetAsync("obj4", json);

                //await cache.SetAsync("obj4", "{ \"Data1\":\"data 3\", \"Data2\":3 }");
                var result2 = await cache.GetAllAsync<SimpleModel>(new[] { "obj1", "obj2", "obj3", "obj4", "obj5" });
                Assert.NotNull(result2);
                Assert.Equal(5, result2.Count);
                Assert.True(result2["obj3"].IsNull);
                Assert.False(result2["obj5"].HasValue);

                await cache.SetAsync("str1", "string 1");
                await cache.SetAsync("str2", "string 2");
                await cache.SetAsync("str3", (string)null);
                var result3 = await cache.GetAllAsync<string>(new[] { "str1", "str2", "str3" });
                Assert.NotNull(result3);
                Assert.Equal(3, result3.Count);
            }
        }

        public virtual async Task CanSetAndGetValue() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                Assert.False((await cache.GetAsync<int>("donkey")).HasValue);
                Assert.False(await cache.ExistsAsync("donkey"));

                SimpleModel nullable = null;
                await cache.SetAsync("nullable", nullable);
                var nullCacheValue = await cache.GetAsync<SimpleModel>("nullable");
                Assert.True(nullCacheValue.HasValue);
                Assert.True(nullCacheValue.IsNull);
                Assert.True(await cache.ExistsAsync("nullable"));

                int? nullableInt = null;
                Assert.False(await cache.ExistsAsync("nullableInt"));
                await cache.SetAsync("nullableInt", nullableInt);
                var nullIntCacheValue = await cache.GetAsync<int?>("nullableInt");
                Assert.True(nullIntCacheValue.HasValue);
                Assert.True(nullIntCacheValue.IsNull);
                Assert.True(await cache.ExistsAsync("nullableInt"));

                await cache.SetAsync("test", 1);
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

                Assert.False(await cache.AddAsync("test", 2));
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

                Assert.True(await cache.ReplaceAsync("test", 2));
                Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);

                Assert.True(await cache.RemoveAsync("test"));
                Assert.False((await cache.GetAsync<int>("test")).HasValue);
                
                Assert.True(await cache.AddAsync("test", 2));
                Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);
                
                Assert.True(await cache.ReplaceAsync("test", new MyData { Message = "Testing" }));
                var result = await cache.GetAsync<MyData>("test");
                Assert.NotNull(result);
                Assert.True(result.HasValue);
                Assert.Equal("Testing", result.Value.Message);
            }
        }
        
        public virtual async Task CanAdd() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                string key = "type-id";
                Assert.False(await cache.ExistsAsync(key));
                Assert.True(await cache.AddAsync(key, true));
                Assert.True(await cache.ExistsAsync(key));
                
                Assert.True(await cache.AddAsync(key + ":1", true, TimeSpan.FromMinutes(1)));
                Assert.True(await cache.ExistsAsync(key + ":1"));

                Assert.False(await cache.AddAsync(key, true, TimeSpan.FromMinutes(1)));
            }
        }

        public virtual async Task CanAddConncurrently() {
            var cache = GetCacheClient();
            if (cache == null)
                return;
            
            using (cache) {
                await cache.RemoveAllAsync();

                var cacheKey = Guid.NewGuid().ToString("N").Substring(10);
                long adds = 0;
                await Run.InParallel(5, async i => {
                    if (await cache.AddAsync(cacheKey, i, TimeSpan.FromMinutes(1)))
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
                var cacheValue = await cache.GetAsync<long>("test");
                Assert.True(cacheValue.HasValue);
                Assert.Equal(1L, cacheValue.Value);

                await cache.SetAsync<long>("test", Int64.MaxValue);
                var cacheValue2 = await cache.GetAsync<int>("test");
                Assert.False(cacheValue2.HasValue);

                cacheValue = await cache.GetAsync<long>("test");
                Assert.True(cacheValue.HasValue);
                Assert.Equal(Int64.MaxValue, cacheValue.Value);

                await cache.SetAsync<MyData>("test", new MyData {
                    Message = "test"
                });
                cacheValue = await cache.GetAsync<long>("test");
                Assert.False(cacheValue.HasValue);
            }
        }

        public virtual async Task CanUseScopedCaches() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
                var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");
                var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

                await cache.SetAsync("test", 1);
                await scopedCache1.SetAsync("test", 2);
                await nestedScopedCache1.SetAsync("test", 3);

                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
                Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
                Assert.Equal(3, (await nestedScopedCache1.GetAsync<int>("test")).Value);

                Assert.Equal(3, (await scopedCache1.GetAsync<int>("nested:test")).Value);
                Assert.Equal(3, (await cache.GetAsync<int>("scoped1:nested:test")).Value);

                // ensure GetAllAsync returns unscoped keys
                Assert.Equal("test", (await scopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
                Assert.Equal("test", (await nestedScopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());

                await scopedCache2.SetAsync("test", 1);

                var result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
                Assert.Equal(2, result);

                // delete without any matching keys
                result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
                Assert.Equal(0, result);

                Assert.False((await scopedCache1.GetAsync<int>("test")).HasValue);
                Assert.False((await nestedScopedCache1.GetAsync<int>("test")).HasValue);
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
                Assert.Equal(1, (await scopedCache2.GetAsync<int>("test")).Value);

                await scopedCache2.RemoveAllAsync();
                Assert.False((await scopedCache1.GetAsync<int>("test")).HasValue);
                Assert.False((await nestedScopedCache1.GetAsync<int>("test")).HasValue);
                Assert.False((await scopedCache2.GetAsync<int>("test")).HasValue);
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
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
                Assert.Equal(1, (await cache.GetAsync<int>(prefix + "test")).Value);
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

                await cache.RemoveByPrefixAsync(prefix);
                Assert.False((await cache.GetAsync<int>(prefix + "test")).HasValue);
                Assert.False((await cache.GetAsync<int>(prefix + "test2")).HasValue);
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
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
                Assert.Equal(dt, cachedValue.Value.Date);
                Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object.");
                Assert.Equal("Hello World", cachedValue.Value.Message);
                Assert.Equal("test", cachedValue.Value.Type);
            }
        }

        public virtual async Task CanSetExpiration() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var expiresAt = SystemClock.UtcNow.AddMilliseconds(300);
                var success = await cache.SetAsync("test", 1, expiresAt);
                Assert.True(success);
                success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(100));
                Assert.True(success);
                Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
                Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

                await SystemClock.SleepAsync(500);
                Assert.False((await cache.GetAsync<int>("test")).HasValue);
                Assert.Null(await cache.GetExpirationAsync("test"));
                Assert.False((await cache.GetAsync<int>("test2")).HasValue);
                Assert.Null(await cache.GetExpirationAsync("test2"));
            }
        }

        public virtual async Task CanIncrementAndExpire() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var success = await cache.SetAsync("test", 0);
                Assert.True(success);

                var expiresIn = TimeSpan.FromSeconds(1);
                var newVal = await cache.IncrementAsync("test", 1, expiresIn);

                Assert.Equal(1, newVal);

                await SystemClock.SleepAsync(1500);
                Assert.False((await cache.GetAsync<int>("test")).HasValue);
            }
        }

        public virtual async Task CanManageSets() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAddAsync(null, 1).AnyContext()).AnyContext();
                await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAddAsync(String.Empty, 1).AnyContext()).AnyContext();

                await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetRemoveAsync(null, 1).AnyContext()).AnyContext();
                await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetRemoveAsync(String.Empty, 1).AnyContext()).AnyContext();

                await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetSetAsync<ICollection<int>>(null).AnyContext()).AnyContext();
                await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetSetAsync<ICollection<int>>(String.Empty).AnyContext()).AnyContext();

                await cache.SetAddAsync("test1", 1).AnyContext();
                await cache.SetAddAsync("test1", 2).AnyContext();
                await cache.SetAddAsync("test1", 3).AnyContext();
                var result = await cache.GetSetAsync<int>("test1").AnyContext();
                Assert.NotNull(result);
                Assert.Equal(3, result.Value.Count);

                await cache.SetRemoveAsync("test1", 2).AnyContext();
                result = await cache.GetSetAsync<int>("test1").AnyContext();
                Assert.NotNull(result);
                Assert.Equal(2, result.Value.Count);

                await cache.SetRemoveAsync("test1", 1).AnyContext();
                await cache.SetRemoveAsync("test1", 3).AnyContext();
                result = await cache.GetSetAsync<int>("test1").AnyContext();
                Assert.NotNull(result);
                Assert.Equal(0, result.Value.Count);
                
                await Assert.ThrowsAnyAsync<Exception>(async () => {
                    await cache.AddAsync("key1", 1).AnyContext();
                    await cache.SetAddAsync("key1", 1).AnyContext();
                }).AnyContext();
            }
        }

        public virtual async Task MeasureThroughput() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var start = SystemClock.UtcNow;
                const int itemCount = 10000;
                var metrics = new InMemoryMetricsClient();
                for (int i = 0; i < itemCount; i++) {
                    await cache.SetAsync("test", 13422);
                    await cache.SetAsync("flag", true);
                    Assert.Equal(13422, (await cache.GetAsync<int>("test")).Value);
                    Assert.Null(await cache.GetAsync<int>("test2"));
                    Assert.True((await cache.GetAsync<bool>("flag")).Value);
                    await metrics.CounterAsync("work");
                }

                var workCounter = metrics.GetCounterStatsAsync("work", start, SystemClock.UtcNow);
            }
        }

        public virtual async Task MeasureSerializerSimpleThroughput() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var start = SystemClock.UtcNow;
                const int itemCount = 10000;
                var metrics = new InMemoryMetricsClient();
                for (int i = 0; i < itemCount; i++) {
                    await cache.SetAsync("test", new SimpleModel {
                                             Data1 = "Hello",
                                             Data2 = 12
                                         });
                    var model = await cache.GetAsync<SimpleModel>("test");
                    Assert.True(model.HasValue);
                    Assert.Equal("Hello", model.Value.Data1);
                    Assert.Equal(12, model.Value.Data2);
                    await metrics.CounterAsync("work");
                }

                var workCounter = metrics.GetCounterStatsAsync("work", start, SystemClock.UtcNow);
            }
        }

        public virtual async Task MeasureSerializerComplexThroughput() {
            var cache = GetCacheClient();
            if (cache == null)
                return;

            using (cache) {
                await cache.RemoveAllAsync();

                var start = SystemClock.UtcNow;
                const int itemCount = 10000;
                var metrics = new InMemoryMetricsClient();
                for (int i = 0; i < itemCount; i++) {
                    await cache.SetAsync("test", new ComplexModel {
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

                    var model = await cache.GetAsync<ComplexModel>("test");
                    Assert.True(model.HasValue);
                    Assert.Equal("Hello", model.Value.Data1);
                    Assert.Equal(12, model.Value.Data2);
                    await metrics.CounterAsync("work");
                }

                var workCounter = metrics.GetCounterStatsAsync("work", start, SystemClock.UtcNow);
            }
        }
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