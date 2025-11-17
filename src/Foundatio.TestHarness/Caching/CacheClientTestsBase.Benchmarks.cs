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
            _logger.LogInformation("Time: {Elapsed:g}", sw.Elapsed);
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
                iterations * 2, sw.ElapsedMilliseconds, (iterations * 2) / sw.Elapsed.TotalSeconds);
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
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test", new SimpleModel { Data1 = "Hello", Data2 = 12 });
                var model = await cache.GetAsync<SimpleModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }

            sw.Stop();
            _logger.LogInformation("Time: {Elapsed:g}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Measures simple object serialization throughput using unique keys.
    /// Separates Set and Get operations for pure throughput measurement without validation overhead.
    /// </summary>
    public virtual async Task Serialization_WithSimpleObjects_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            const int iterations = 1000;
            var model = new SimpleModel { Data1 = "Test", Data2 = 42 };
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                await cache.SetAsync($"simple{i}", model);
            }

            for (int i = 0; i < iterations; i++)
            {
                await cache.GetAsync<SimpleModel>($"simple{i}");
            }

            sw.Stop();
            _logger.LogInformation(
                "Simple serializer throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                iterations * 2, sw.ElapsedMilliseconds, (iterations * 2) / sw.Elapsed.TotalSeconds);
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
            _logger.LogInformation("Time: {Elapsed:g}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Measures complex object serialization throughput using unique keys.
    /// Tests nested objects, lists, and dictionaries with separated Set/Get for pure performance measurement.
    /// </summary>
    public virtual async Task Serialization_WithComplexObjects_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            const int iterations = 1000;
            var model = new ComplexModel
            {
                Data1 = "Test",
                Data2 = 42,
                Data3 = true,
                Simple = new SimpleModel { Data1 = "Nested", Data2 = 100 },
                Simples = new List<SimpleModel>
                {
                    new SimpleModel { Data1 = "Item1", Data2 = 1 },
                    new SimpleModel { Data1 = "Item2", Data2 = 2 }
                },
                DictionarySimples = new Dictionary<string, SimpleModel>
                {
                    ["key1"] = new SimpleModel { Data1 = "Dict1", Data2 = 10 },
                    ["key2"] = new SimpleModel { Data1 = "Dict2", Data2 = 20 }
                }
            };

            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                await cache.SetAsync($"complex{i}", model);
            }

            for (int i = 0; i < iterations; i++)
            {
                await cache.GetAsync<ComplexModel>($"complex{i}");
            }

            sw.Stop();
            _logger.LogInformation(
                "Complex serializer throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                iterations * 2, sw.ElapsedMilliseconds, (iterations * 2) / sw.Elapsed.TotalSeconds);
        }
    }
}
