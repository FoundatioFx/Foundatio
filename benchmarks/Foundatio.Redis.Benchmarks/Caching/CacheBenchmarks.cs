using System;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;
using Foundatio.Messaging;
using StackExchange.Redis;

namespace Foundatio.Benchmarks.Caching {
    public class CacheBenchmarks {
        private const int ITEM_COUNT = 100;
        private readonly ICacheClient _inMemoryCache = new InMemoryCacheClient();
        private readonly ICacheClient _redisCache;
        private readonly ICacheClient _hybridCacheClient;

        public CacheBenchmarks() {
            var muxer = ConnectionMultiplexer.Connect("localhost");
            _redisCache = new RedisCacheClient(muxer);
            _redisCache.RemoveAllAsync().GetAwaiter().GetResult();
            _hybridCacheClient = new HybridCacheClient(_redisCache, new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), Topic = "test-cache" }));
        }

        [Benchmark]
        public void ProcessInMemoryCache() {
            Process(_inMemoryCache);
        }

        [Benchmark]
        public void ProcessRedisCache() {
            Process(_redisCache);
        }

        [Benchmark]
        public void ProcessHybridRedisCache() {
            Process(_hybridCacheClient);
        }

        [Benchmark]
        public void ProcessInMemoryCacheWithConstantInvalidation() {
            Process(_inMemoryCache, true);
        }

        [Benchmark]
        public void ProcessRedisCacheWithConstantInvalidation() {
            Process(_redisCache, true);
        }

        [Benchmark]
        public void ProcessHybridRedisCacheWithConstantInvalidation() {
            Process(_hybridCacheClient, true);
        }

        private void Process(ICacheClient cache, bool useSingleKey = false) {
            try {
                for (int i = 0; i < ITEM_COUNT; i++) { 
                    string key = useSingleKey ? "test" : String.Concat("test", i);
                    cache.SetAsync(key, new CacheItem { Id = i }, TimeSpan.FromHours(1)).GetAwaiter().GetResult();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }

            try {
                for (int i = 0; i < ITEM_COUNT; i++) {
                    string key = useSingleKey ? "test" : String.Concat("test", i);
                    var entry = cache.GetAsync<string>(key).GetAwaiter().GetResult();
                }

                for (int i = 0; i < ITEM_COUNT; i++) {
                    string key = useSingleKey ? "test" : "test0";
                    var entry = cache.GetAsync<string>(key).GetAwaiter().GetResult();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }
        }
    }

    public class CacheItem {
        public int Id { get; set; }
    }
}
