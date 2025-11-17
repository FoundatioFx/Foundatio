using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Foundatio.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase : TestWithLoggingBase
{
    protected CacheClientTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    protected virtual ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return null;
    }

    public virtual async Task SetUnixTimeSecondsAsync_WithUtcDateTime_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromSeconds(1));
            long unixTimeValue = value.ToUnixTimeSeconds();

            Assert.True(await cache.SetUnixTimeSecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
        }
    }

    public virtual async Task GetUnixTimeSecondsAsync_WithUtcDateTime_ReturnsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromSeconds(1));

            await cache.SetUnixTimeSecondsAsync("test", value);
            var actual = await cache.GetUnixTimeSecondsAsync("test");

            Assert.Equal(value.Ticks, actual.Ticks);
            Assert.Equal(TimeSpan.Zero, actual.Offset);
        }
    }

    public virtual async Task SetUnixTimeMillisecondsAsync_WithLocalDateTime_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.Now.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            Assert.True(await cache.SetUnixTimeMillisecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
        }
    }

    public virtual async Task GetUnixTimeMillisecondsAsync_WithLocalDateTime_ReturnsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.Now.Floor(TimeSpan.FromMilliseconds(1));

            await cache.SetUnixTimeMillisecondsAsync("test", value);
            var actual = (await cache.GetUnixTimeMillisecondsAsync("test")).ToLocalTime();

            Assert.Equal(value.Ticks, actual.Ticks);
        }
    }

    public virtual async Task GetUnixTimeMillisecondsAsync_WithUtcDateTime_ReturnsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            await cache.SetUnixTimeMillisecondsAsync("test", value);
            var actual = await cache.GetUnixTimeMillisecondsAsync("test");

            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, actual);
        }
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
    public SampleDictionary<string, SimpleModel> DerivedDictionarySimples { get; set; }
}

public class MyData
{
    private readonly string _blah = "blah";
    public string Blah => _blah;
    public string Type { get; set; }
    public DateTimeOffset Date { get; set; }
    public string Message { get; set; }
}

public class SampleDictionary<TKey, TValue> : IDictionary<TKey, TValue>
{
    private readonly IDictionary<TKey, TValue> _dictionary;

    public SampleDictionary()
    {
        _dictionary = new Dictionary<TKey, TValue>();
    }

    public SampleDictionary(IDictionary<TKey, TValue> dictionary)
    {
        _dictionary = new Dictionary<TKey, TValue>(dictionary);
    }

    public SampleDictionary(IEqualityComparer<TKey> comparer)
    {
        _dictionary = new Dictionary<TKey, TValue>(comparer);
    }

    public SampleDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer)
    {
        _dictionary = new Dictionary<TKey, TValue>(dictionary, comparer);
    }

    public void Add(TKey key, TValue value)
    {
        _dictionary.Add(key, value);
    }

    public void Add(KeyValuePair<TKey, TValue> item)
    {
        _dictionary.Add(item);
    }

    public bool Remove(TKey key)
    {
        return _dictionary.Remove(key);
    }

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        return _dictionary.Remove(item);
    }

    public void Clear()
    {
        _dictionary.Clear();
    }

    public bool ContainsKey(TKey key)
    {
        return _dictionary.ContainsKey(key);
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
    {
        return _dictionary.Contains(item);
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        return _dictionary.TryGetValue(key, out value);
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        _dictionary.CopyTo(array, arrayIndex);
    }

    public ICollection<TKey> Keys => _dictionary.Keys;

    public ICollection<TValue> Values => _dictionary.Values;

    public int Count => _dictionary.Count;

    public bool IsReadOnly => _dictionary.IsReadOnly;

    public TValue this[TKey key]
    {
        get => _dictionary[key];
        set => _dictionary[key] = value;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        return _dictionary.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
