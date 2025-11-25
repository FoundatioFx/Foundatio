using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    /// <summary>
    /// Measures cache operation throughput by performing 10,000 iterations of Set/Get operations with assertions.
    /// Tests multiple primitive types (int, bool) and validates correctness during performance measurement.
    /// </summary>
    public virtual async Task CacheOperations_WithMultipleTypes_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test", 13422);
                await cache.SetAsync("flag", true);
                Assert.Equal(13422, (await cache.GetAsync<int>("test")).Value);
                Assert.False((await cache.GetAsync<int>("test2")).HasValue);
                Assert.True((await cache.GetAsync<bool>("flag")).Value);
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                itemCount * 5, sw.ElapsedMilliseconds, itemCount * 5 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures cache throughput with simple Set/Get operations using unique keys.
    /// Separates Set and Get operations for independent throughput measurement without assertions.
    /// </summary>
    public virtual async Task CacheOperations_WithRepeatedSetAndGet_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            const int iterations = 1000;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                await cache.SetAsync($"key{i}", i);
            }

            for (int i = 0; i < iterations; i++)
            {
                await cache.GetAsync<int>($"key{i}");
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                iterations * 2, sw.ElapsedMilliseconds, iterations * 2 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures serialization throughput with simple objects (10,000 iterations).
    /// Tests Set/Get operations with assertions to validate serialization correctness under load.
    /// </summary>
    public virtual async Task Serialization_WithSimpleObjectsAndValidation_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int iterations = 10000;
            for (int i = 0; i < iterations; i++)
            {
                await cache.SetAsync("test", new SimpleModel { Data1 = "Hello", Data2 = 12 });
                var model = await cache.GetAsync<SimpleModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                iterations * 2, sw.ElapsedMilliseconds, iterations * 2 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures serialization throughput with complex nested objects (10,000 iterations).
    /// Tests objects with nested models, lists, and dictionaries while validating correctness.
    /// </summary>
    public virtual async Task Serialization_WithComplexObjectsAndValidation_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test",
                    new ComplexModel
                    {
                        Data1 = "Hello",
                        Data2 = 12,
                        Data3 = true,
                        Simple = new SimpleModel { Data1 = "hi", Data2 = 13 },
                        Simples =
                            new List<SimpleModel>
                            {
                                new SimpleModel { Data1 = "hey", Data2 = 45 },
                                new SimpleModel { Data1 = "next", Data2 = 3423 }
                            },
                        DictionarySimples =
                            new Dictionary<string, SimpleModel> { { "sdf", new SimpleModel { Data1 = "Sachin" } } },
                        DerivedDictionarySimples =
                            new SampleDictionary<string, SimpleModel>
                            {
                                { "sdf", new SimpleModel { Data1 = "Sachin" } }
                            }
                    });

                var model = await cache.GetAsync<ComplexModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                itemCount * 2, sw.ElapsedMilliseconds, itemCount * 2 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures SetAllAsync/GetAllAsync throughput with 9,999 keys in a single batch operation.
    /// Tests bulk insert and retrieval performance while validating data correctness.
    /// </summary>
    public virtual async Task SetAllAsync_WithLargeNumberOfKeys_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const int keyCount = 9999;
            var items = new Dictionary<string, int>();
            for (int i = 0; i < keyCount; i++)
                items[$"key{i}"] = i;

            var sw = Stopwatch.StartNew();

            int result = await cache.SetAllAsync(items, TimeSpan.FromHours(1));
            Assert.Equal(keyCount, result);

            var keys = new List<string>();
            for (int i = 0; i < keyCount; i++)
                keys.Add($"key{i}");

            var results = await cache.GetAllAsync<int>(keys);
            Assert.Equal(keyCount, results.Count);

            sw.Stop();

            for (int i = 0; i < keyCount; i++)
                Assert.Equal(i, results[$"key{i}"].Value);

            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                keyCount * 2, sw.ElapsedMilliseconds, keyCount * 2 / sw.Elapsed.TotalSeconds);
        }
    }
}
