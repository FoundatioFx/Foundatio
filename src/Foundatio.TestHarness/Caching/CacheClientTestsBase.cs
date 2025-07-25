using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public abstract class CacheClientTestsBase : TestWithLoggingBase
{
    protected CacheClientTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    protected virtual ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return null;
    }

    public virtual async Task CanGetAllAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test1", 1);
            await cache.SetAsync("test2", 2);
            await cache.SetAsync("test3", 3);
            var result = await cache.GetAllAsync<int>(["test1", "test2", "test3"]);
            Assert.NotNull(result);
            Assert.Equal(3, result.Count);
            Assert.Equal(1, result["test1"].Value);
            Assert.Equal(2, result["test2"].Value);
            Assert.Equal(3, result["test3"].Value);

            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            await cache.SetAsync("obj2", new SimpleModel { Data1 = "data 2", Data2 = 2 });
            await cache.SetAsync("obj3", (SimpleModel)null);
            await cache.SetAsync("obj4", new SimpleModel { Data1 = "test 1", Data2 = 4 });

            var result2 = await cache.GetAllAsync<SimpleModel>(["obj1", "obj2", "obj3", "obj4", "obj5"]);
            Assert.NotNull(result2);
            Assert.Equal(5, result2.Count);
            Assert.True(result2["obj3"].IsNull);
            Assert.False(result2["obj5"].HasValue);

            var obj4 = result2["obj4"];
            Assert.NotNull(obj4);
            Assert.Equal("test 1", obj4.Value.Data1);

            await cache.SetAsync("str1", "string 1");
            await cache.SetAsync("str2", "string 2");
            await cache.SetAsync("str3", (string)null);
            var result3 = await cache.GetAllAsync<string>(["str1", "str2", "str3"]);
            Assert.NotNull(result3);
            Assert.Equal(3, result3.Count);
        }
    }

    public virtual async Task CanGetAllWithOverlapAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test1", 1.0);
            await cache.SetAsync("test2", 2.0);
            await cache.SetAsync("test3", 3.0);
            await cache.SetAllAsync(new Dictionary<string, double> {
                { "test3", 3.5 },
                { "test4", 4.0 },
                { "test5", 5.0 }
            });

            var result = await cache.GetAllAsync<double>(["test1", "test2", "test3", "test4", "test5"]);
            Assert.NotNull(result);
            Assert.Equal(5, result.Count);
            Assert.Equal(1.0, result["test1"].Value);
            Assert.Equal(2.0, result["test2"].Value);
            Assert.Equal(3.5, result["test3"].Value);
            Assert.Equal(4.0, result["test4"].Value);
            Assert.Equal(5.0, result["test5"].Value);
        }
    }

    public virtual async Task CanSetAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(3, await cache.ListAddAsync("set", new List<int> { 1, 1, 2, 3 }));
            var result = await cache.GetListAsync<int>("set");
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            Assert.True(await cache.ListRemoveAsync("set", 1));
            result = await cache.GetListAsync<int>("set");
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);
        }
    }

    public virtual async Task CanSetAndGetValueAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
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

    public virtual async Task CanAddAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "type-id";
            const string val = "value-should-not-change";
            Assert.False(await cache.ExistsAsync(key));
            Assert.True(await cache.AddAsync(key, val));
            Assert.True(await cache.ExistsAsync(key));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);

            Assert.False(await cache.AddAsync(key, "random value"));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);

            // Add a sub key
            Assert.True(await cache.AddAsync(key + ":1", "nested"));
            Assert.True(await cache.ExistsAsync(key + ":1"));
            Assert.Equal("nested", (await cache.GetAsync<string>(key + ":1")).Value);
        }
    }

    public virtual async Task CanAddConcurrentlyAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string cacheKey = Guid.NewGuid().ToString("N").Substring(10);
            long adds = 0;

            await Parallel.ForEachAsync(Enumerable.Range(1, 5), async (i, _) =>
            {
                if (await cache.AddAsync(cacheKey, i, TimeSpan.FromMinutes(1)))
                    Interlocked.Increment(ref adds);
            });

            Assert.Equal(1, adds);
        }
    }

    public virtual async Task CanGetAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<int>("test", 1);
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);

            await cache.SetAsync<long>("test", 1);
            var cacheValue2 = await cache.GetAsync<int>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(1L, cacheValue2.Value);

            await cache.SetAsync<long>("test", Int64.MaxValue);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var cacheValue3 = await cache.GetAsync<int>("test");
                Assert.False(cacheValue3.HasValue);
            });

            cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue.Value);
        }
    }

    public virtual async Task CanTryGetAsync()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<int>("test", 1);
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);

            await cache.SetAsync<long>("test", 1);
            var cacheValue2 = await cache.GetAsync<int>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(1L, cacheValue2.Value);

            await cache.SetAsync<long>("test", Int64.MaxValue);
            var cacheValue3 = await cache.GetAsync<int>("test");
            Assert.False(cacheValue3.HasValue);

            cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue.Value);

            await cache.SetAsync<MyData>("test", new MyData
            {
                Message = "test"
            });
            cacheValue = await cache.GetAsync<long>("test");
            Assert.False(cacheValue.HasValue);
        }
    }

    public virtual async Task CanUseScopedCachesAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
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

            int result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
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

            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(10, await scopedCache1.IncrementAsync("total", 10));
            Assert.Equal(10, await scopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(20, await nestedScopedCache1.IncrementAsync("total", 20));
            Assert.Equal(20, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(1, await nestedScopedCache1.RemoveAllAsync(["id", "total"]));
            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(1, await scopedCache1.RemoveAllAsync(["id", "total"]));
            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
        }
    }

    public virtual async Task CanRemoveAllAsync()
    {
        const int COUNT = 10000;

        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, await cache.RemoveAllAsync());

            var dictionary = Enumerable.Range(0, COUNT).ToDictionary(i => $"remove-all:{i}");

            var sw = Stopwatch.StartNew();
            await cache.SetAllAsync(dictionary);
            sw.Stop();
            _logger.LogInformation("Set All Time: {Elapsed:g}", sw.Elapsed);

            sw = Stopwatch.StartNew();
            Assert.Equal(COUNT, await cache.RemoveAllAsync());
            sw.Stop();
            _logger.LogInformation("Remove All Time: {Elapsed:g}", sw.Elapsed);

            Assert.False(await cache.ExistsAsync("remove-all:0"));
            Assert.False(await cache.ExistsAsync($"remove-all:{COUNT - 1}"));
        }
    }

    public virtual async Task CanRemoveAllKeysAsync()
    {
        const int COUNT = 10000;

        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dictionary = Enumerable.Range(0, COUNT).ToDictionary(i => $"remove-all-keys:{i}");

            var sw = Stopwatch.StartNew();
            await cache.SetAllAsync(dictionary);
            sw.Stop();
            _logger.LogInformation("Set All Time: {Elapsed:g}", sw.Elapsed);

            sw = Stopwatch.StartNew();
            Assert.Equal(COUNT, await cache.RemoveAllAsync(dictionary.Keys));
            sw.Stop();
            _logger.LogInformation("Remove All Time: {Elapsed:g}", sw.Elapsed);

            Assert.False(await cache.ExistsAsync("remove-all-keys:0"));
            Assert.False(await cache.ExistsAsync($"remove-all-keys:{COUNT - 1}"));
        }
    }

    public virtual async Task CanRemoveByPrefixAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string prefix = "blah:";
            await cache.SetAsync("test", 1);
            await cache.SetAsync(prefix + "test", 1);
            await cache.SetAsync(prefix + "test2", 2);

            Assert.Equal(0, await cache.RemoveByPrefixAsync(prefix + ":doesntexist"));
            Assert.Equal(2, await cache.RemoveByPrefixAsync(prefix));
            Assert.False(await cache.ExistsAsync(prefix + "test"));
            Assert.False(await cache.ExistsAsync(prefix + "test2"));
            Assert.True(await cache.ExistsAsync("test"));

            Assert.Equal(1, await cache.RemoveByPrefixAsync(String.Empty));
        }
    }

    public static IEnumerable<object[]> GetRegexSpecialCharacters()
    {
        return
        [
            ["*"],
            ["+"],
            ["?"],
            ["^"],
            ["$"],
            ["|"],
            ["\\"],
            ["["],
            ["]"],
            ["{"],
            ["}"],
            ["("],
            [")"],
            ["))"],  // Invalid regex - extra closing parentheses
            ["(("],  // Invalid regex - extra opening parentheses
            ["]]"],  // Invalid regex - extra closing brackets
            ["[["],  // Invalid regex - extra opening brackets
            ["(()"], // Invalid regex - unbalanced parentheses
            ["([)]"], // Invalid regex - incorrectly nested
            ["[{}]"], // Invalid regex - brackets with braces inside
            ["{{}"],  // Invalid regex - unbalanced braces
            ["+++"],  // Invalid regex - multiple plus operators
            ["***"],  // Invalid regex - multiple asterisks
            ["???"]   // Invalid regex - multiple question marks
        ];
    }

    public virtual async Task CanRemoveByPrefixWithRegexCharactersAsync(string specialChar)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string regexPrefix = $"regex{specialChar}:";
            await cache.SetAsync($"{regexPrefix}test1", 1);
            await cache.SetAsync($"{regexPrefix}test2", 2);
            await cache.SetAsync($"other{specialChar}test", 3);

            Assert.Equal(2, await cache.RemoveByPrefixAsync(regexPrefix));
            Assert.False(await cache.ExistsAsync($"{regexPrefix}test1"));
            Assert.False(await cache.ExistsAsync($"{regexPrefix}test2"));
            Assert.True(await cache.ExistsAsync($"other{specialChar}test"));
        }
    }

    public static IEnumerable<object[]> GetWildcardPatterns()
    {
        return
        [
            ["**:"],
            ["*.*"],
            ["*.*:"],
            ["*.txt:"],
            ["**/**:"],
            ["glob*.*:"],
            ["pattern**suffix:"]
        ];
    }

    public virtual async Task CanRemoveByPrefixWithWildcardPatternsAsync(string pattern)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync($"{pattern}test1", 1);
            await cache.SetAsync($"{pattern}test2", 2);
            await cache.SetAsync($"similar{pattern}test", 3);
            await cache.SetAsync($"not{pattern.Replace("*", "X")}test", 4);

            Assert.Equal(2, await cache.RemoveByPrefixAsync(pattern));
            Assert.False(await cache.ExistsAsync($"{pattern}test1"));
            Assert.False(await cache.ExistsAsync($"{pattern}test2"));
            Assert.True(await cache.ExistsAsync($"similar{pattern}test"));
            Assert.True(await cache.ExistsAsync($"not{pattern.Replace("*", "X")}test"));
        }
    }

    public virtual async Task CanRemoveByPrefixWithDoubleAsteriskAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("**:test1", 1);
            await cache.SetAsync("**:test2", 2);
            await cache.SetAsync("*:test3", 3);
            await cache.SetAsync("***:test4", 4);

            // * is treated as a wildcard so everything before it would be removed.
            Assert.Equal(4, await cache.RemoveByPrefixAsync("**:"));
            Assert.False(await cache.ExistsAsync("**:test1"));
            Assert.False(await cache.ExistsAsync("**:test2"));
            Assert.False(await cache.ExistsAsync("*:test3"));
            Assert.False(await cache.ExistsAsync("***:test4"));
        }
    }



    public static IEnumerable<object[]> GetSpecialPrefixes()
    {
        return
        [
            ["space test:"],
            ["tab\t:"],
            ["newline\n:"],
            ["unicode_测试:"],
            ["emoji_🔥:"],
            ["double::colon:"],
            ["dots...:"],
            ["dashes---:"],
            ["underscores___:"],
            ["mixed_sp3c!@l#:"],
            ["percent%encoded:"],
            ["json{\"key\":\"value\"}:"],
            ["xml<tag>:</tag>"],
            ["url://protocol:"],
            ["query?param=value:"],
            ["fragment#anchor:"],
            ["ampersand&and:"],
            ["equals=sign:"],
            ["semicolon;sep:"],
            ["comma,sep:"],
            ["quotes\"single':"],
            ["backtick`:"],
            ["tilde~:"],
            ["exclamation!:"],
            ["at@symbol:"],
            ["hash#tag:"],
            ["dollar$sign:"],
            ["caret^symbol:"],
            ["ampersand&symbol:"],
            ["asterisk*symbol:"],
            ["parentheses():"],
            ["minus-dash:"],
            ["plus+sign:"],
            ["equals=symbol:"],
            ["brackets[]:"],
            ["braces{}:"],
            ["backslash\\:"],
            ["pipe|symbol:"],
            ["less<than:"],
            ["greater>than:"],
            ["question?mark:"],
            ["forwardslash/:"],
            ["period.dot:"]
        ];
    }

    public virtual async Task CanRemoveByPrefixWithSpecialCharactersAsync(string specialPrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync($"{specialPrefix}test1", 1);
            await cache.SetAsync($"{specialPrefix}test2", 2);
            await cache.SetAsync($"other{specialPrefix}test", 3);

            int removed = await cache.RemoveByPrefixAsync(specialPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{specialPrefix}test1"));
            Assert.False(await cache.ExistsAsync($"{specialPrefix}test2"));
            Assert.True(await cache.ExistsAsync($"other{specialPrefix}test"));
        }
    }

    public virtual async Task CanRemoveByPrefixWithNullAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", 1);

            // Null prefix should remove all keys (equivalent to empty prefix)
            int removed = await cache.RemoveByPrefixAsync(null);
            Assert.Equal(1, removed);
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task CanRemoveByPrefixWithEmptyStringAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", 1);

            // Empty prefix should remove all keys
            int removed = await cache.RemoveByPrefixAsync("");
            Assert.Equal(1, removed);
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public static IEnumerable<object[]> GetWhitespaceOnlyPrefixes()
    {
        return
        [
            ["   "],
            ["\t"]
        ];
    }

    public virtual async Task CanRemoveByPrefixWithWhitespaceAsync(string whitespacePrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up test data - one other key to verify it remains
            await cache.SetAsync("other:test", 1);

            // Create keys that actually match the whitespace prefix
            await cache.SetAsync($"{whitespacePrefix}match1", 10);
            await cache.SetAsync($"{whitespacePrefix}match2", 20);

            // Whitespace prefixes are treated as a valid wildcard prefix and everything is removed.
            int removed = await cache.RemoveByPrefixAsync(whitespacePrefix);
            Assert.Equal(3, removed);
            Assert.False(await cache.ExistsAsync($"{whitespacePrefix}match1"));
            Assert.False(await cache.ExistsAsync($"{whitespacePrefix}match2"));
            Assert.False(await cache.ExistsAsync("other:test"));
        }
    }

    public static IEnumerable<object[]> GetLineEndingPrefixes()
    {
        return
        [
            ["\n"],
            ["\r"],
            ["\r\n"]
        ];
    }

    public virtual async Task CanRemoveByPrefixWithLineEndingsAsync(string lineEndingPrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up test data - one other key to verify it remains
            await cache.SetAsync("other:test", 1);

            // Create keys that actually match the line ending prefix
            await cache.SetAsync($"{lineEndingPrefix}match1", 10);
            await cache.SetAsync($"{lineEndingPrefix}match2", 20);

            // Line ending prefixes are treated as a valid wildcard prefix.
            int removed = await cache.RemoveByPrefixAsync(lineEndingPrefix);
            Assert.Equal(3, removed);
            Assert.False(await cache.ExistsAsync($"{lineEndingPrefix}match1"));
            Assert.False(await cache.ExistsAsync($"{lineEndingPrefix}match2"));
            Assert.False(await cache.ExistsAsync("other:test"));
        }
    }

    public virtual async Task CanRemoveByPrefixWithScopedCachesAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");

            const string cacheKey = "key";
            await cache.SetAsync(cacheKey, 1);
            await scopedCache1.SetAsync(cacheKey, 1);
            Assert.Equal(1, (await cache.GetAsync<int>(cacheKey)).Value);
            Assert.Equal(1, (await scopedCache1.GetAsync<int>(cacheKey)).Value);

            // Remove by prefix should only remove the unscoped cache.
            Assert.Equal(1, await cache.RemoveByPrefixAsync(cacheKey));
            Assert.False(await cache.ExistsAsync(cacheKey));
            Assert.True(await scopedCache1.ExistsAsync(cacheKey));
            Assert.Equal(1, (await scopedCache1.GetAsync<int>(cacheKey)).Value);

            // Add the unscoped cache value back.
            await cache.SetAsync(cacheKey, 1);

            // Remove by null key.
            Assert.Equal(1, await scopedCache1.RemoveByPrefixAsync(null));
            Assert.True(await cache.ExistsAsync(cacheKey));
            Assert.False(await scopedCache1.ExistsAsync(cacheKey));

            // Add the scoped cache value back.
            await scopedCache1.SetAsync(cacheKey, 1);

            Assert.Equal(2, await cache.RemoveByPrefixAsync(null));
            Assert.False(await cache.ExistsAsync(cacheKey));
            Assert.False(await scopedCache1.ExistsAsync(cacheKey));

            // Reset client values
            await cache.SetAsync(cacheKey, 1);
            await scopedCache1.SetAsync(cacheKey, 1);

            // Remove by empty key.
            Assert.Equal(1, await scopedCache1.RemoveByPrefixAsync(String.Empty));
            Assert.True(await cache.ExistsAsync(cacheKey));
            Assert.False(await scopedCache1.ExistsAsync(cacheKey));

            // Add the scoped cache value back.
            await scopedCache1.SetAsync(cacheKey, 1);

            Assert.Equal(2, await cache.RemoveByPrefixAsync(String.Empty));
            Assert.False(await cache.ExistsAsync(cacheKey));
            Assert.False(await scopedCache1.ExistsAsync(cacheKey));

            // Reset client values
            await cache.SetAsync(cacheKey, 1);
            await scopedCache1.SetAsync(cacheKey, 1);

            // Remove by *.
            Assert.Equal(1, await scopedCache1.RemoveByPrefixAsync("*"));
            Assert.True(await cache.ExistsAsync(cacheKey));
            Assert.False(await scopedCache1.ExistsAsync(cacheKey));

            // Reset client values
            await scopedCache1.SetAsync(cacheKey, 1);

            Assert.Equal(2, await cache.RemoveByPrefixAsync("*"));
            Assert.False(await cache.ExistsAsync(cacheKey));
            Assert.False(await scopedCache1.ExistsAsync(cacheKey));
        }
    }

    public virtual async Task CanRemoveByPrefixMultipleEntriesAsync(int count)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string prefix = "prefix:";
            await cache.SetAsync("test", 1);

            await cache.SetAllAsync(Enumerable.Range(0, count).ToDictionary(i => $"{prefix}test{i}"));

            Assert.Equal(1, (await cache.GetAsync<int>($"{prefix}test1")).Value);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.Equal(0, await cache.RemoveByPrefixAsync($"{prefix}:doesntexist"));
            Assert.Equal(count, await cache.RemoveByPrefixAsync(prefix));
        }
    }

    public virtual async Task CanSetAndGetObjectAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dt = DateTimeOffset.Now;
            var value = new MyData
            {
                Type = "test",
                Date = dt,
                Message = "Hello World"
            };
            await cache.SetAsync("test", value);
            value.Type = "modified";
            var cachedValue = await cache.GetAsync<MyData>("test");
            Assert.NotNull(cachedValue);
            Assert.Equal(dt, cachedValue.Value.Date);
            Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object");
            Assert.Equal("Hello World", cachedValue.Value.Message);
            Assert.Equal("test", cachedValue.Value.Type);
        }
    }

    public virtual async Task CanSetExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
            bool success = await cache.SetAsync("test", 1, expiresAt);
            Assert.True(success);
            success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(100));
            Assert.True(success);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

            await Task.Delay(500);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
            Assert.Null(await cache.GetExpirationAsync("test"));
            Assert.False((await cache.GetAsync<int>("test2")).HasValue);
            Assert.Null(await cache.GetExpirationAsync("test2"));
        }
    }

    public virtual async Task CanSetMinMaxExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var timeProvider = new FakeTimeProvider();
            var utcNow = DateTime.UtcNow;
            timeProvider.SetUtcNow(utcNow);

            var expires = DateTime.MaxValue - utcNow.AddDays(1);
            Assert.True(await cache.SetAsync("test1", 1, expires));
            Assert.Equal(1, (await cache.GetAsync<int>("test1")).Value);
            var actualExpiration = await cache.GetExpirationAsync("test1");
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expires.Subtract(TimeSpan.FromSeconds(10)), expires);

            // MinValue expires items.
            Assert.False(await cache.SetAsync("test2", 1, DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("test2"));

            // MaxValue never expires.
            Assert.True(await cache.SetAsync("test3", 1, DateTime.MaxValue));
            Assert.Equal(1, (await cache.GetAsync<int>("test3")).Value);
            actualExpiration = await cache.GetExpirationAsync("test3");
            Assert.NotNull(actualExpiration);

            // Really high expiration value.
            Assert.True(await cache.SetAsync("test4", 1, DateTime.MaxValue - utcNow.AddDays(-1)));
            Assert.Equal(1, (await cache.GetAsync<int>("test4")).Value);
            actualExpiration = await cache.GetExpirationAsync("test4");
            Assert.NotNull(actualExpiration);

            // No Expiration
            Assert.True(await cache.SetAsync("test5", 1));
            Assert.Null(await cache.GetExpirationAsync("test5"));

            // Expire in an hour.
            var expiration = utcNow.AddHours(1);
            await cache.SetExpirationAsync("test5", expiration);
            actualExpiration = await cache.GetExpirationAsync("test5");
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expiration - expiration.Subtract(TimeSpan.FromSeconds(5)), expiration - utcNow);

            // Change expiration to MaxValue.
            await cache.SetExpirationAsync("test5", DateTime.MaxValue);
            Assert.NotNull(actualExpiration);

            // Change expiration to MinValue.
            await cache.SetExpirationAsync("test5", DateTime.MinValue);
            Assert.Null(await cache.GetExpirationAsync("test5"));
            Assert.False(await cache.ExistsAsync("test5"));

            // Ensure keys are not added as they are already expired
            Assert.Equal(0, await cache.SetAllAsync(new Dictionary<string, object>
            {
                { "test6", 1 },
                { "test7", 1 },
                { "test8", 1 }
            }, DateTime.MinValue));

            // Expire time right now
            Assert.False(await cache.SetAsync("test9", 1, utcNow));
            Assert.False(await cache.ExistsAsync("test9"));
            Assert.Null(await cache.GetExpirationAsync("test9"));
        }
    }

    public virtual async Task CanIncrementAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.SetAsync("test", 0));
            Assert.Equal(1, await cache.IncrementAsync("test"));
            Assert.Equal(1, await cache.IncrementAsync("test1"));
            Assert.Equal(0, await cache.IncrementAsync("test3", 0));

            // The following is not supported by redis.
            if (cache is InMemoryCacheClient)
            {
                Assert.True(await cache.SetAsync("test2", "stringValue"));
                Assert.Equal(1, await cache.IncrementAsync("test2"));
            }
        }
    }

    public virtual async Task CanIncrementAndExpireAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            bool success = await cache.SetAsync("test", 0);
            Assert.True(success);

            var expiresIn = TimeSpan.FromSeconds(1);
            double newVal = await cache.IncrementAsync("test", 1, expiresIn);

            Assert.Equal(1, newVal);

            await Task.Delay(1500);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
        }
    }

    public virtual async Task SetAllShouldExpireAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var expiry = TimeSpan.FromMilliseconds(50);
            await cache.SetAllAsync(new Dictionary<string, object> { { "test", "value" } }, expiry);

            // Add 10ms to the expiry to ensure the cache has expired as the delay window is not guaranteed to be exact.
            await Task.Delay(expiry.Add(TimeSpan.FromMilliseconds(10)));

            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task CanReplaceIfEqual()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);
            Assert.Null(await cache.GetExpirationAsync(cacheKey));

            Assert.False(await cache.ReplaceIfEqualAsync(cacheKey, "456", "789", TimeSpan.FromHours(1)));
            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123", TimeSpan.FromHours(1)));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("456", result.Value);
            Assert.NotNull(await cache.GetExpirationAsync(cacheKey));
        }
    }

    public virtual async Task CanRemoveIfEqual()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("remove-if-equal", "123"));
            var result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            Assert.False(await cache.RemoveIfEqualAsync("remove-if-equal", "789"));
            Assert.True(await cache.RemoveIfEqualAsync("remove-if-equal", "123"));
            result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task CanRoundTripLargeNumbersAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));

            double lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task CanGetAndSetDateTimeAsync()
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
            var actual = await cache.GetUnixTimeSecondsAsync("test");
            Assert.Equal(value.Ticks, actual.Ticks);
            Assert.Equal(TimeSpan.Zero, actual.Offset);

            value = DateTime.Now.Floor(TimeSpan.FromMilliseconds(1));
            unixTimeValue = value.ToUnixTimeMilliseconds();
            Assert.True(await cache.SetUnixTimeMillisecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            actual = (await cache.GetUnixTimeMillisecondsAsync("test")).ToLocalTime();
            Assert.Equal(value.Ticks, actual.Ticks);

            value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            unixTimeValue = value.ToUnixTimeMilliseconds();
            Assert.True(await cache.SetUnixTimeMillisecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            actual = await cache.GetUnixTimeMillisecondsAsync("test");
            Assert.Equal(value.Ticks, actual.Ticks);
            Assert.Equal(TimeSpan.Zero, actual.Offset);

            var lowerValue = value - TimeSpan.FromHours(1);
            long lowerUnixTimeValue = lowerValue.ToUnixTimeMilliseconds();
            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds, await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerUnixTimeValue, await cache.GetAsync<long>("test", 0));

            await cache.RemoveAsync("test");

            Assert.Equal(unixTimeValue, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value.AddHours(1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            await cache.RemoveAsync("test");

            Assert.Equal(unixTimeValue, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", value.AddHours(-1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            var higherValue = value + TimeSpan.FromHours(1);
            long higherUnixTimeValue = higherValue.ToUnixTimeMilliseconds();
            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds, await cache.SetIfHigherAsync("test", higherValue));
            Assert.Equal(higherUnixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(higherValue, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task CanRoundTripLargeNumbersWithExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var minExpiration = TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(59)).Add(TimeSpan.FromSeconds(55));
            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            double lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));
        }
    }

    public virtual async Task CanManageListsAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(String.Empty, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(key, null as List<int>));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(String.Empty, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(key, null as List<int>));

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(null));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(String.Empty));

            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            await cache.ListRemoveAsync(key, [1, 2, 3]);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);

            await cache.RemoveAllAsync();

            // Add an empty item.
            await cache.ListAddAsync<int>(key, []);

            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            await cache.ListRemoveAsync(key, 2);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);

            await cache.ListRemoveAsync(key, 1);
            await cache.ListRemoveAsync(key, 3);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await cache.AddAsync("key1", 1);
                await cache.ListAddAsync("key1", 1);
            });
        }
    }

    public virtual async Task CanManageListsWithNullItemsAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(key, null as List<string>));
            Assert.Equal(0, await cache.ListAddAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListAddAsync(key, ["1", null]));
            var result = await cache.GetListAsync<string>(key);
            Assert.NotNull(result);
            Assert.Single(result.Value);

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(key, null as List<string>));
            Assert.Equal(0, await cache.ListRemoveAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListRemoveAsync(key, ["1", null]));
            result = await cache.GetListAsync<string>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    /// <summary>
    /// single strings don't get handled as char arrays
    /// </summary>
    public virtual async Task CanManageStringListsAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:strings";

            await cache.ListAddAsync(key, "my-value");
            var stringResult = await cache.GetListAsync<string>(key);
            Assert.Single(stringResult.Value);
            Assert.Equal("my-value", stringResult.Value.First());

            await cache.ListRemoveAsync(key, "my-value");
            stringResult = await cache.GetListAsync<string>(key);
            Assert.Empty(stringResult.Value);
        }
    }

    public virtual async Task CanManageListPagingAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            CacheValue<ICollection<int>> pagedResult;
            var firstPageResults = new HashSet<int>(5);
            var actualResults = new HashSet<int>(values.Length);

            for (int page = 1; page < values.Length / 5 + 1; page++)
            {
                pagedResult = await cache.GetListAsync<int>(key, page, 5);
                Assert.NotNull(pagedResult);
                Assert.Equal(5, pagedResult.Value.Count);
                actualResults.AddRange(pagedResult.Value);

                if (page is 1)
                    firstPageResults.AddRange(pagedResult.Value);
            }

            // Use a higher expiration so we can assert the new items are returned last in the list.
            await cache.ListAddAsync(key, [21, 22], TimeSpan.FromMinutes(2));
            pagedResult = await cache.GetListAsync<int>(key, 5, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(2, pagedResult.Value.Count);
            actualResults.AddRange(pagedResult.Value);
            Assert.Equal(values.Length + 2, actualResults.Count);

            // Assert invalid starting page is empty.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetListAsync<int>(key, 0, 5));

            // Assert invalid page is empty
            pagedResult = await cache.GetListAsync<int>(key, 6, 5);
            Assert.NotNull(pagedResult);
            Assert.Empty(pagedResult.Value);

            // Assert the first page is the same.
            pagedResult = await cache.GetListAsync<int>(key, 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(firstPageResults, pagedResult.Value.ToArray());
        }
    }

    public virtual async Task CanManageGetListExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:get";

            Assert.Equal(1, await cache.ListAddAsync(key, [1], TimeSpan.FromMilliseconds(100)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(1));

            await Task.Delay(150);

            // GetList should invalidate expired items
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.False(cacheValue.HasValue);
            Assert.False(await cache.ExistsAsync(key));
        }
    }
    public virtual async Task CanManageListAddExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add";

            Assert.Equal(1, await cache.ListAddAsync(key, [1]));

            // Remove the expired item via Add.
            Assert.Equal(0, await cache.ListAddAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.False(await cache.ExistsAsync(key));

            // Add with expiration
            Assert.Equal(1, await cache.ListAddAsync(key, [2], TimeSpan.FromMilliseconds(100)));
            Assert.Equal(1, await cache.ListAddAsync(key, [3], TimeSpan.FromMilliseconds(175)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Equal(2, cacheValue.Value.Count);
            Assert.True(cacheValue.Value.Contains(2));
            Assert.True(cacheValue.Value.Contains(3));

            await Task.Delay(125);
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(3));

            await Task.Delay(100);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task CanManageListRemoveExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:remove";

            Assert.Equal(2, await cache.ListAddAsync(key, [1, 2]));

            // Past expiration just calls remove on the item if it's there.
            Assert.Equal(1, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.Equal(0, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(2));

            Assert.Equal(1, await cache.ListRemoveAsync(key, [2], TimeSpan.FromSeconds(1)));
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task MeasureThroughputAsync()
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

    public virtual async Task MeasureSerializerSimpleThroughputAsync()
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
                await cache.SetAsync("test", new SimpleModel
                {
                    Data1 = "Hello",
                    Data2 = 12
                });
                var model = await cache.GetAsync<SimpleModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }
            sw.Stop();
            _logger.LogInformation("Time: {Elapsed:g}", sw.Elapsed);
        }
    }

    public virtual async Task MeasureSerializerComplexThroughputAsync()
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
                await cache.SetAsync("test", new ComplexModel
                {
                    Data1 = "Hello",
                    Data2 = 12,
                    Data3 = true,
                    Simple = new SimpleModel
                    {
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
            }
            sw.Stop();
            _logger.LogInformation("Time: {Elapsed:g}", sw.Elapsed);
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
