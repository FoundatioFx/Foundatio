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

    public virtual async Task GetAllAsync_WithExistingKeys_ReturnsAllValues()
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
        }
    }

    public virtual async Task GetAllAsync_WithMixedObjectTypes_ReturnsCorrectValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            await cache.SetAsync("obj2", new SimpleModel { Data1 = "data 2", Data2 = 2 });
            await cache.SetAsync("obj4", new SimpleModel { Data1 = "test 1", Data2 = 4 });

            var result = await cache.GetAllAsync<SimpleModel>(["obj1", "obj2", "obj4"]);
            Assert.NotNull(result);
            Assert.Equal(3, result.Count);

            var obj4 = result["obj4"];
            Assert.NotNull(obj4);
            Assert.Equal("test 1", obj4.Value.Data1);
        }
    }

    public virtual async Task GetAllAsync_WithNullValues_HandlesNullsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("obj3", (SimpleModel)null);
            var result = await cache.GetAllAsync<SimpleModel>(["obj3"]);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.True(result["obj3"].IsNull);

            await cache.SetAsync("str1", "string 1");
            await cache.SetAsync("str3", (string)null);
            var result2 = await cache.GetAllAsync<string>(["str1", "str3"]);
            Assert.NotNull(result2);
            Assert.Equal(2, result2.Count);
        }
    }

    public virtual async Task GetAllAsync_WithNonExistentKeys_ReturnsEmptyResults()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            var result = await cache.GetAllAsync<SimpleModel>(["obj1", "obj5"]);
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.False(result["obj5"].HasValue);
        }
    }

    public virtual async Task GetAllAsync_WithOverlappingKeys_UsesLatestValues()
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
            await cache.SetAllAsync(new Dictionary<string, double>
            {
                { "test3", 3.5 }, { "test4", 4.0 }, { "test5", 5.0 }
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

    public virtual async Task ListAddAsync_WithDuplicates_RemovesDuplicatesAndAddsItems()
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

    public virtual async Task GetAsync_WithNonExistentKey_ReturnsNoValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.False((await cache.GetAsync<int>("donkey")).HasValue);
            Assert.False(await cache.ExistsAsync("donkey"));
        }
    }

    public virtual async Task SetAsync_WithNullReferenceType_StoresAsNullValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            SimpleModel nullable = null;
            await cache.SetAsync("nullable", nullable);
            var nullCacheValue = await cache.GetAsync<SimpleModel>("nullable");
            Assert.True(nullCacheValue.HasValue);
            Assert.True(nullCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullable"));
        }
    }

    public virtual async Task SetAsync_WithNullValueType_StoresAsNullValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            int? nullableInt = null;
            Assert.False(await cache.ExistsAsync("nullableInt"));
            await cache.SetAsync("nullableInt", nullableInt);
            var nullIntCacheValue = await cache.GetAsync<int?>("nullableInt");
            Assert.True(nullIntCacheValue.HasValue);
            Assert.True(nullIntCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task ExistsAsync_WithNullStoredValue_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            SimpleModel nullable = null;
            await cache.SetAsync("nullable", nullable);
            Assert.True(await cache.ExistsAsync("nullable"));

            int? nullableInt = null;
            await cache.SetAsync("nullableInt", nullableInt);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task AddAsync_WithNewKey_ReturnsTrue()
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
        }
    }

    public virtual async Task AddAsync_WithExistingKey_ReturnsFalseAndPreservesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "type-id";
            const string val = "value-should-not-change";
            await cache.AddAsync(key, val);

            Assert.False(await cache.AddAsync(key, "random value"));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);
        }
    }

    public virtual async Task AddAsync_WithNestedKeyUsingSeparator_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "type-id";

            Assert.True(await cache.AddAsync(key + ":1", "nested"));
            Assert.True(await cache.ExistsAsync(key + ":1"));
            Assert.Equal("nested", (await cache.GetAsync<string>(key + ":1")).Value);
        }
    }

    public virtual async Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
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

    public virtual async Task GetAsync_WithNumericTypeConversion_ConvertsIntToLong()
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
        }
    }

    public virtual async Task GetAsync_WithNumericTypeConversion_ConvertsLongToInt()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<long>("test", 1);
            var cacheValue = await cache.GetAsync<int>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);
        }
    }

    public virtual async Task GetAsync_WithMaxLongAsInt_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<long>("test", Int64.MaxValue);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var cacheValue = await cache.GetAsync<int>("test");
                Assert.False(cacheValue.HasValue);
            });

            var cacheValue2 = await cache.GetAsync<long>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue2.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemanticsAndIntAsLong_ConvertsSuccessfully()
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
        }
    }

    public virtual async Task GetAsync_WithTryGetSemanticsAndMaxLongAsInt_ReturnsNoValue()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<long>("test", Int64.MaxValue);
            var cacheValue3 = await cache.GetAsync<int>("test");
            Assert.False(cacheValue3.HasValue);

            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemanticsAndComplexTypeAsLong_ReturnsNoValue()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<MyData>("test", new MyData { Message = "test" });
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.False(cacheValue.HasValue);
        }
    }

    public virtual async Task SetAsync_WithDifferentScopes_IsolatesKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await scopedCache2.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task SetAsync_WithNestedScopes_PreservesHierarchy()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await nestedScopedCache1.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await nestedScopedCache1.GetAsync<int>("test")).Value);

            Assert.Equal(3, (await scopedCache1.GetAsync<int>("nested:test")).Value);
            Assert.Equal(3, (await cache.GetAsync<int>("scoped1:nested:test")).Value);
        }
    }

    public virtual async Task GetAllAsync_WithScopedCache_ReturnsUnscopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            await scopedCache1.SetAsync("test", 1);
            await nestedScopedCache1.SetAsync("test", 2);

            Assert.Equal("test", (await scopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
            Assert.Equal("test", (await nestedScopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithScopedCache_AffectsOnlyScopedKeys()
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
            await scopedCache2.SetAsync("test", 4);

            int result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
            Assert.Equal(2, result);

            // delete without any matching keys
            result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
            Assert.Equal(0, result);

            Assert.False((await scopedCache1.GetAsync<int>("test")).HasValue);
            Assert.False((await nestedScopedCache1.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(4, (await scopedCache2.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            await scopedCache2.RemoveAllAsync();
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.False((await scopedCache2.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task IncrementAsync_WithScopedCache_WorksWithinScope()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

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

    public virtual async Task RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently()
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

    public virtual async Task RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys()
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

    public virtual async Task RemoveByPrefixAsync_WithNonMatchingPrefix_RemovesZeroKeys()
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
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyPrefixedKeys()
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

            Assert.Equal(2, await cache.RemoveByPrefixAsync(prefix));
            Assert.False(await cache.ExistsAsync(prefix + "test"));
            Assert.False(await cache.ExistsAsync(prefix + "test2"));
            Assert.True(await cache.ExistsAsync("test"));
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
            ["))"], // Invalid regex - extra closing parentheses
            ["(("], // Invalid regex - extra opening parentheses
            ["]]"], // Invalid regex - extra closing brackets
            ["[["], // Invalid regex - extra opening brackets
            ["(()"], // Invalid regex - unbalanced parentheses
            ["([)]"], // Invalid regex - incorrectly nested
            ["[{}]"], // Invalid regex - brackets with braces inside
            ["{{}"], // Invalid regex - unbalanced braces
            ["+++"], // Invalid regex - multiple plus operators
            ["***"], // Invalid regex - multiple asterisks
            ["???"] // Invalid regex - multiple question marks
        ];
    }

    [Theory]
    [MemberData(nameof(GetRegexSpecialCharacters))]
    public virtual async Task RemoveByPrefixAsync_WithRegexMetacharacter_TreatsAsLiteral(string specialChar)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string regexPrefix = $"pattern{specialChar}:";
            await cache.SetAsync($"{regexPrefix}searchResult", 100);
            await cache.SetAsync($"{regexPrefix}matchResult", 200);
            await cache.SetAsync($"unrelated{specialChar}data", 300);

            int removed = await cache.RemoveByPrefixAsync(regexPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{regexPrefix}searchResult"));
            Assert.False(await cache.ExistsAsync($"{regexPrefix}matchResult"));
            Assert.True(await cache.ExistsAsync($"unrelated{specialChar}data"));
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

    [Theory]
    [MemberData(nameof(GetWildcardPatterns))]
    public virtual async Task RemoveByPrefixAsync_WithWildcardPattern_TreatsAsLiteral(string pattern)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync($"{pattern}fileA", 1000);
            await cache.SetAsync($"{pattern}fileB", 2000);
            await cache.SetAsync($"different{pattern}item", 3000);
            await cache.SetAsync($"excluded{pattern.Replace("*", "X")}item", 4000);

            int removed = await cache.RemoveByPrefixAsync(pattern);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{pattern}fileA"));
            Assert.False(await cache.ExistsAsync($"{pattern}fileB"));
            Assert.True(await cache.ExistsAsync($"different{pattern}item"));
            Assert.True(await cache.ExistsAsync($"excluded{pattern.Replace("*", "X")}item"));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithDoubleAsteriskPrefix_TreatsAsLiteral()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("**:globMatch1", 100);
            await cache.SetAsync("**:globMatch2", 200);
            await cache.SetAsync("*:singleWildcard", 300);
            await cache.SetAsync("***:tripleAsterisk", 400);

            int removed = await cache.RemoveByPrefixAsync("**:");
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync("**:globMatch1"));
            Assert.False(await cache.ExistsAsync("**:globMatch2"));
            Assert.True(await cache.ExistsAsync("*:singleWildcard"));
            Assert.True(await cache.ExistsAsync("***:tripleAsterisk"));
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

    [Theory]
    [MemberData(nameof(GetSpecialPrefixes))]
    public virtual async Task RemoveByPrefixAsync_WithSpecialCharacterPrefix_TreatsAsLiteral(string specialPrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync($"{specialPrefix}encodedValue", 100);
            await cache.SetAsync($"{specialPrefix}escapedString", 200);
            await cache.SetAsync($"unmatched{specialPrefix}entry", 300);

            int removed = await cache.RemoveByPrefixAsync(specialPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{specialPrefix}encodedValue"));
            Assert.False(await cache.ExistsAsync($"{specialPrefix}escapedString"));
            Assert.True(await cache.ExistsAsync($"unmatched{specialPrefix}entry"));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithNullPrefix_RemovesAllKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("userId", 1);
            await cache.SetAsync("sessionId", 2);
            await cache.SetAsync("productId", 3);

            int removed = await cache.RemoveByPrefixAsync(null);
            Assert.Equal(3, removed);
            Assert.False(await cache.ExistsAsync("userId"));
            Assert.False(await cache.ExistsAsync("sessionId"));
            Assert.False(await cache.ExistsAsync("productId"));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithEmptyPrefix_RemovesAllKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("orderId", 100);
            await cache.SetAsync("customerId", 200);
            await cache.SetAsync("invoiceId", 300);

            int removed = await cache.RemoveByPrefixAsync("");
            Assert.Equal(3, removed);
            Assert.False(await cache.ExistsAsync("orderId"));
            Assert.False(await cache.ExistsAsync("customerId"));
            Assert.False(await cache.ExistsAsync("invoiceId"));
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

    [Theory]
    [MemberData(nameof(GetWhitespaceOnlyPrefixes))]
    public virtual async Task RemoveByPrefixAsync_WithWhitespacePrefix_TreatsAsLiteral(string whitespacePrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("accountId", 100);
            await cache.SetAsync($"{whitespacePrefix}filteredResult1", 200);
            await cache.SetAsync($"{whitespacePrefix}filteredResult2", 300);

            int removed = await cache.RemoveByPrefixAsync(whitespacePrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{whitespacePrefix}filteredResult1"));
            Assert.False(await cache.ExistsAsync($"{whitespacePrefix}filteredResult2"));
            Assert.True(await cache.ExistsAsync("accountId"));
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

    [Theory]
    [MemberData(nameof(GetLineEndingPrefixes))]
    public virtual async Task RemoveByPrefixAsync_WithLineEndingPrefix_TreatsAsLiteral(string lineEndingPrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("logEntry", 1000);
            await cache.SetAsync($"{lineEndingPrefix}parsedLine1", 2000);
            await cache.SetAsync($"{lineEndingPrefix}parsedLine2", 3000);

            int removed = await cache.RemoveByPrefixAsync(lineEndingPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{lineEndingPrefix}parsedLine1"));
            Assert.False(await cache.ExistsAsync($"{lineEndingPrefix}parsedLine2"));
            Assert.True(await cache.ExistsAsync("logEntry"));
        }
    }

    [Theory]
    [InlineData("snowboard", 1, true)] // Exact key match
    [InlineData("s", 1, true)] // Partial prefix match
    [InlineData(null, 1, false)] // Null prefix (all keys in scope)
    [InlineData("", 1, false)] // Empty prefix (all keys in scope)
    public virtual async Task RemoveByPrefixAsync_FromScopedCache_RemovesOnlyScopedKeys(string prefixToRemove,
        int expectedRemovedCount, bool shouldUnscopedRemain)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            Assert.Equal(1, (await cache.GetAsync<int>(key)).Value);
            Assert.Equal(1, (await scopedCache.GetAsync<int>(key)).Value);

            // Remove by prefix from scoped cache
            Assert.Equal(expectedRemovedCount, await scopedCache.RemoveByPrefixAsync(prefixToRemove));

            // Verify unscoped cache state
            Assert.Equal(shouldUnscopedRemain, await cache.ExistsAsync(key));

            // Verify scoped cache item was removed
            Assert.False(await scopedCache.ExistsAsync(key));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public virtual async Task RemoveByPrefixAsync_NullOrEmptyPrefixWithScopedCache_RemovesCorrectKeys(string prefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            // Remove by null/empty from scoped cache - should only remove within scope
            Assert.Equal(1, await scopedCache.RemoveByPrefixAsync(prefix));
            Assert.True(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));

            // Add the scoped cache value back
            await scopedCache.SetAsync(key, 1);

            // Remove by null/empty from unscoped cache - should remove both unscoped and scoped
            Assert.Equal(2, await cache.RemoveByPrefixAsync(prefix));
            Assert.False(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));
        }
    }

    [Fact]
    public virtual async Task RemoveByPrefixAsync_AsteriskPrefixWithScopedCache_TreatedAsLiteral()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            // Remove by "*" from scoped cache - should not match "snowboard"
            Assert.Equal(0, await scopedCache.RemoveByPrefixAsync("*"));
            Assert.True(await cache.ExistsAsync(key));
            Assert.True(await scopedCache.ExistsAsync(key));

            // Remove by "*" from unscoped cache - should not match "snowboard"
            Assert.Equal(0, await cache.RemoveByPrefixAsync("*"));
            Assert.True(await cache.ExistsAsync(key));
            Assert.True(await scopedCache.ExistsAsync(key));
        }
    }

    [Fact]
    public virtual async Task RemoveByPrefixAsync_PartialPrefixWithScopedCache_RemovesMatchingKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            // Remove by partial prefix "s" from scoped cache
            Assert.Equal(1, await scopedCache.RemoveByPrefixAsync("s"));
            Assert.True(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));

            // Add the scoped cache value back
            await scopedCache.SetAsync(key, 1);

            // Remove by partial prefix "s" from unscoped cache - should remove both
            Assert.Equal(2, await cache.RemoveByPrefixAsync("s"));
            Assert.False(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));
        }
    }

    [Theory]
    [InlineData(10)] // Small dataset
    [InlineData(100)] // Medium dataset
    [InlineData(1000)] // Large dataset
    public virtual async Task RemoveByPrefixAsync_WithMultipleMatchingKeys_RemovesOnlyPrefixedKeys(int count)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string prefix = "product:";
            const string unmatchedKey = "order";
            await cache.SetAsync(unmatchedKey, 1);

            await cache.SetAllAsync(Enumerable.Range(0, count).ToDictionary(i => $"{prefix}item{i}"));

            Assert.Equal(1, (await cache.GetAsync<int>($"{prefix}item1")).Value);
            Assert.Equal(1, (await cache.GetAsync<int>(unmatchedKey)).Value);

            // Verify non-existent prefix removal returns 0
            Assert.Equal(0, await cache.RemoveByPrefixAsync($"{prefix}doesntexist"));

            // Verify removal of all matching prefix keys
            Assert.Equal(count, await cache.RemoveByPrefixAsync(prefix));

            // Verify only unmatched key remains
            Assert.True(await cache.ExistsAsync(unmatchedKey));
        }
    }

    public virtual async Task SetAsync_WithComplexObject_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dt = DateTimeOffset.Now;
            var value = new MyData { Type = "test", Date = dt, Message = "Hello World" };

            await cache.SetAsync("test", value);

            Assert.True(await cache.ExistsAsync("test"));
            var cachedValue = await cache.GetAsync<MyData>("test");
            Assert.NotNull(cachedValue);
            Assert.True(cachedValue.HasValue);
        }
    }

    public virtual async Task GetAsync_WithComplexObject_ReturnsNewInstance()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var value = new MyData { Type = "test", Date = DateTimeOffset.Now, Message = "Hello World" };

            await cache.SetAsync("test", value);
            value.Type = "modified";

            var cachedValue = await cache.GetAsync<MyData>("test");
            Assert.NotNull(cachedValue);
            Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object");
            Assert.Equal("test", cachedValue.Value.Type);
            Assert.NotEqual("modified", cachedValue.Value.Type);
        }
    }

    public virtual async Task GetAsync_WithComplexObject_PreservesAllProperties()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dt = DateTimeOffset.Now;
            var value = new MyData { Type = "test", Date = dt, Message = "Hello World" };

            await cache.SetAsync("test", value);
            var cachedValue = await cache.GetAsync<MyData>("test");

            Assert.NotNull(cachedValue);
            Assert.Equal("test", cachedValue.Value.Type);
            Assert.Equal(dt, cachedValue.Value.Date);
            Assert.Equal("Hello World", cachedValue.Value.Message);
        }
    }

    public virtual async Task GetExpirationAsync_WithNoExpiration_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("no-expiration", "value");
            var expiration = await cache.GetExpirationAsync("no-expiration");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithExpiration_ReturnsCorrectTimeSpan()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddHours(1);
            await cache.SetAsync("with-expiration", "value", expiresAt);
            var expiration = await cache.GetExpirationAsync("with-expiration");

            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59),
                TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(10)));
        }
    }

    public virtual async Task GetExpirationAsync_WithNonExistentKey_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiration = await cache.GetExpirationAsync("non-existent-key");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithExpiredKey_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var quickExpiry = DateTime.UtcNow.AddMilliseconds(100);
            await cache.SetAsync("quick-expiry", "value", quickExpiry);
            await Task.Delay(200);
            var expiration = await cache.GetExpirationAsync("quick-expiry");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetAllExpirationAsync_WithMixedKeys_ReturnsOnlyKeysWithExpiration()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1, TimeSpan.FromMinutes(5));
            await cache.SetAsync("key2", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("key3", 3); // No expiration
            await cache.SetAsync("key4", 4, TimeSpan.FromMinutes(15));

            // Act
            var expirations = await cache.GetAllExpirationAsync(["key1", "key2", "key3", "key4", "key5"]);

            // Assert
            Assert.NotNull(expirations);
            Assert.Equal(3, expirations.Count); // key3 has no expiration, key5 doesn't exist

            Assert.True(expirations.ContainsKey("key1"));
            Assert.NotNull(expirations["key1"]);
            Assert.True(expirations["key1"].Value > TimeSpan.FromMinutes(4));
            Assert.True(expirations["key1"].Value <= TimeSpan.FromMinutes(5));

            Assert.True(expirations.ContainsKey("key2"));
            Assert.NotNull(expirations["key2"]);
            Assert.True(expirations["key2"].Value > TimeSpan.FromMinutes(9));
            Assert.True(expirations["key2"].Value <= TimeSpan.FromMinutes(10));

            Assert.False(expirations.ContainsKey("key3")); // No expiration
            Assert.False(expirations.ContainsKey("key5")); // Doesn't exist

            Assert.True(expirations.ContainsKey("key4"));
            Assert.NotNull(expirations["key4"]);
            Assert.True(expirations["key4"].Value > TimeSpan.FromMinutes(14));
            Assert.True(expirations["key4"].Value <= TimeSpan.FromMinutes(15));
        }
    }

    [Theory]
    [InlineData(10)] // Small dataset
    [InlineData(100)] // Medium dataset
    [InlineData(1000)] // Large dataset
    public virtual async Task GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(int count)
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var keys = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string key = $"perf-test-key-{i}";
                keys.Add(key);
                await cache.SetAsync(key, i, TimeSpan.FromMinutes(i % 60 + 1));
            }

            // Act
            var sw = Stopwatch.StartNew();
            var expirations = await cache.GetAllExpirationAsync(keys);
            sw.Stop();

            _logger.LogInformation("Get All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Assert
            Assert.Equal(count, expirations.Count);
            Assert.All(expirations, kvp => Assert.NotNull(kvp.Value));
        }
    }

    public virtual async Task GetAllExpirationAsync_WithExpiredKeys_ExcludesExpiredKeys()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1, TimeSpan.FromMilliseconds(100));
            await cache.SetAsync("key2", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("key3", 3, TimeSpan.FromMilliseconds(100));

            // Wait for key1 and key3 to expire
            await Task.Delay(200);

            // Act
            var expirations = await cache.GetAllExpirationAsync(["key1", "key2", "key3"]);

            // Assert
            Assert.NotNull(expirations);
            Assert.Single(expirations); // Only key2 should be returned
            Assert.False(expirations.ContainsKey("key1")); // Expired
            Assert.True(expirations.ContainsKey("key2")); // Still valid
            Assert.False(expirations.ContainsKey("key3")); // Expired

            var key2Expiration = expirations["key2"];
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));
        }
    }

    public virtual async Task SetAsync_WithShortExpiration_ExpiresCorrectly()
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
            Assert.False((await cache.GetAsync<int>("test2")).HasValue);
        }
    }

    public virtual async Task GetExpirationAsync_AfterExpiry_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
            await cache.SetAsync("test", 1, expiresAt);

            await Task.Delay(500);
            var expiration = await cache.GetExpirationAsync("test");

            Assert.Null(expiration);
        }
    }

    public virtual async Task SetAsync_WithExpirationEdgeCases_HandlesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;
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
            Assert.InRange(actualExpiration.Value, expiration - expiration.Subtract(TimeSpan.FromSeconds(5)),
                expiration - utcNow);

            // Change expiration to MaxValue.
            await cache.SetExpirationAsync("test5", DateTime.MaxValue);
            Assert.NotNull(actualExpiration);

            // Change expiration to MinValue.
            await cache.SetExpirationAsync("test5", DateTime.MinValue);
            Assert.Null(await cache.GetExpirationAsync("test5"));
            Assert.False(await cache.ExistsAsync("test5"));

            // Ensure keys are not added as they are already expired
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "test6", 1 }, { "test7", 1 }, { "test8", 1 } },
                    DateTime.MinValue));

            // Expire time right now
            Assert.False(await cache.SetAsync("test9", 1, utcNow));
            Assert.False(await cache.ExistsAsync("test9"));
            Assert.Null(await cache.GetExpirationAsync("test9"));
        }
    }

    public virtual async Task SetExpirationAsync_WithDateTimeMinValue_ExpiresImmediately()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // MinValue should expire items immediately
            Assert.False(await cache.SetAsync("test", 1, DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task SetExpirationAsync_WithDateTimeMaxValue_NeverExpires()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // MaxValue should never expire
            Assert.True(await cache.SetAsync("test", 1, DateTime.MaxValue));
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            var actualExpiration = await cache.GetExpirationAsync("test");
            Assert.NotNull(actualExpiration);
        }
    }

    public virtual async Task SetExpirationAsync_WithCurrentTime_ExpiresImmediately()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;

            // Expiration time set to now should expire immediately
            Assert.False(await cache.SetAsync("test", 1, utcNow));
            Assert.False(await cache.ExistsAsync("test"));
            Assert.Null(await cache.GetExpirationAsync("test"));
        }
    }

    public virtual async Task SetExpirationAsync_ChangingFromNoExpirationToFutureTime_UpdatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;

            // Set with no expiration
            Assert.True(await cache.SetAsync("test", 1));
            Assert.Null(await cache.GetExpirationAsync("test"));

            // Update to expire in an hour
            var expiration = utcNow.AddHours(1);
            await cache.SetExpirationAsync("test", expiration);
            var actualExpiration = await cache.GetExpirationAsync("test");
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expiration - expiration.Subtract(TimeSpan.FromSeconds(5)),
                expiration - utcNow);
        }
    }

    public virtual async Task SetExpirationAsync_ChangingToDateTimeMinValue_RemovesKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set with future expiration
            Assert.True(await cache.SetAsync("test", 1, DateTime.UtcNow.AddHours(1)));
            Assert.True(await cache.ExistsAsync("test"));

            // Change expiration to MinValue should remove the key
            await cache.SetExpirationAsync("test", DateTime.MinValue);
            Assert.Null(await cache.GetExpirationAsync("test"));
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task SetAllAsync_WithDateTimeMinValue_DoesNotAddKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Ensure keys are not added when they are already expired
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "test1", 1 }, { "test2", 2 }, { "test3", 3 } },
                    DateTime.MinValue));

            Assert.False(await cache.ExistsAsync("test1"));
            Assert.False(await cache.ExistsAsync("test2"));
            Assert.False(await cache.ExistsAsync("test3"));
        }
    }

    public virtual async Task SetAllExpiration_WithMultipleKeys_SetsExpirationForAll()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1);
            await cache.SetAsync("key2", 2);
            await cache.SetAsync("key3", 3);

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "key1", TimeSpan.FromMinutes(5) },
                { "key2", TimeSpan.FromMinutes(10) },
                { "key3", TimeSpan.FromMinutes(15) }
            };

            // Act
            await cache.SetAllExpirationAsync(expirations);

            // Assert
            var key1Expiration = await cache.GetExpirationAsync("key1");
            Assert.NotNull(key1Expiration);
            Assert.True(key1Expiration.Value > TimeSpan.FromMinutes(4));
            Assert.True(key1Expiration.Value <= TimeSpan.FromMinutes(5));

            var key2Expiration = await cache.GetExpirationAsync("key2");
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));

            var key3Expiration = await cache.GetExpirationAsync("key3");
            Assert.NotNull(key3Expiration);
            Assert.True(key3Expiration.Value > TimeSpan.FromMinutes(14));
            Assert.True(key3Expiration.Value <= TimeSpan.FromMinutes(15));
        }
    }

    public virtual async Task SetAllExpiration_WithNullValues_RemovesExpiration()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1, TimeSpan.FromMinutes(5));
            await cache.SetAsync("key2", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("key3", 3, TimeSpan.FromMinutes(15));

            // Verify initial expirations are set
            Assert.NotNull(await cache.GetExpirationAsync("key1"));
            Assert.NotNull(await cache.GetExpirationAsync("key2"));
            Assert.NotNull(await cache.GetExpirationAsync("key3"));

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "key1", null }, // Remove expiration
                { "key2", TimeSpan.FromMinutes(20) }, // Change expiration
                { "key3", null } // Remove expiration
            };

            // Act
            await cache.SetAllExpirationAsync(expirations);

            // Assert
            Assert.Null(await cache.GetExpirationAsync("key1")); // Expiration removed
            Assert.True(await cache.ExistsAsync("key1")); // Key still exists

            var key2Expiration = await cache.GetExpirationAsync("key2");
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(19));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(20));

            Assert.Null(await cache.GetExpirationAsync("key3")); // Expiration removed
            Assert.True(await cache.ExistsAsync("key3")); // Key still exists
        }
    }

    public virtual async Task SetAllExpiration_WithLargeNumberOfKeys_SetsAllExpirations(int count)
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var keys = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string key = $"perf-test-key-{i}";
                keys.Add(key);
                await cache.SetAsync(key, i);
            }

            var expirations = new Dictionary<string, TimeSpan?>();
            for (int i = 0; i < count; i++)
            {
                expirations[keys[i]] = TimeSpan.FromMinutes(i % 60 + 1);
            }

            // Act
            var sw = Stopwatch.StartNew();
            await cache.SetAllExpirationAsync(expirations);
            sw.Stop();

            _logger.LogInformation("Set All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Assert - verify a sample of keys
            var key0Expiration = await cache.GetExpirationAsync(keys[0]);
            Assert.NotNull(key0Expiration);
            Assert.True(key0Expiration.Value <= TimeSpan.FromMinutes(1));

            var keySampleIndex = count / 2;
            var keySampleExpiration = await cache.GetExpirationAsync(keys[keySampleIndex]);
            Assert.NotNull(keySampleExpiration);
            Assert.True(keySampleExpiration.Value <= TimeSpan.FromMinutes(41));
        }
    }

    public virtual async Task SetAllExpiration_WithNonExistentKeys_HandlesGracefully()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1);
            await cache.SetAsync("key2", 2);

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "key1", TimeSpan.FromMinutes(5) },
                { "key2", TimeSpan.FromMinutes(10) },
                { "nonexistent", TimeSpan.FromMinutes(15) } // This key doesn't exist
            };

            // Act
            await cache.SetAllExpirationAsync(expirations);

            // Assert
            var key1Expiration = await cache.GetExpirationAsync("key1");
            Assert.NotNull(key1Expiration);
            Assert.True(key1Expiration.Value > TimeSpan.FromMinutes(4));
            Assert.True(key1Expiration.Value <= TimeSpan.FromMinutes(5));

            var key2Expiration = await cache.GetExpirationAsync("key2");
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));

            // Non-existent key should not be created
            Assert.False(await cache.ExistsAsync("nonexistent"));
            Assert.Null(await cache.GetExpirationAsync("nonexistent"));
        }
    }

    public virtual async Task IncrementAsync_WithExistingKey_IncrementsValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.SetAsync("test", 0));
            Assert.Equal(1, await cache.IncrementAsync("test"));
        }
    }

    public virtual async Task IncrementAsync_WithNonExistentKey_InitializesToOne()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(1, await cache.IncrementAsync("test1"));
        }
    }

    public virtual async Task IncrementAsync_WithSpecifiedAmount_IncrementsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(0, await cache.IncrementAsync("test3", 0));
        }
    }

    public virtual async Task IncrementAsync_WithStringValue_ConvertsAndIncrements()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            if (cache is InMemoryCacheClient)
            {
                Assert.True(await cache.SetAsync("test2", "stringValue"));
                Assert.Equal(1, await cache.IncrementAsync("test2"));
            }
            else
            {
                throw new NotSupportedException("Only supported by InMemoryCacheClient.");
            }
        }
    }

    public virtual async Task IncrementAsync_WithExpiration_ExpiresCorrectly()
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

    public virtual async Task SetAllAsync_WithExpiration_KeysExpireCorrectly()
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

    public virtual async Task ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-test";
            Assert.True(await cache.AddAsync(cacheKey, "original"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("original", result.Value);

            Assert.True(await cache.ReplaceAsync(cacheKey, "replaced"));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("replaced", result.Value);
        }
    }

    public virtual async Task ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "non-existent";
            Assert.False(await cache.ReplaceAsync(cacheKey, "value"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task ReplaceAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-expiration";
            Assert.True(await cache.AddAsync(cacheKey, "initial"));
            Assert.Null(await cache.GetExpirationAsync(cacheKey));

            Assert.True(await cache.ReplaceAsync(cacheKey, "updated", TimeSpan.FromHours(1)));
            var expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.NotNull(expiration);
            Assert.True(expiration.Value > TimeSpan.Zero);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace()
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

            Assert.False(await cache.ReplaceIfEqualAsync(cacheKey, "456", "789"));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));

            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("456", result.Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));
            Assert.Null(await cache.GetExpirationAsync(cacheKey));

            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123", TimeSpan.FromHours(1)));
            Assert.NotNull(await cache.GetExpirationAsync(cacheKey));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove()
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
            result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("remove-if-equal", "123"));

            Assert.True(await cache.RemoveIfEqualAsync("remove-if-equal", "123"));
            var result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task SetAsync_WithLargeNumber_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value));
        }
    }

    public virtual async Task GetAsync_WithLargeNumber_ReturnsCorrectValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            await cache.SetAsync("test", value);
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task SetIfLowerAsync_WithLargeNumbers_UpdatesWhenLower()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            await cache.SetAsync("test", value);

            double lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task SetIfHigherAsync_WithLargeNumbers_UpdatesWhenHigher()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            double lowerValue = value - 1000;
            await cache.SetAsync("test", lowerValue);

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
        }
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

    public virtual async Task SetIfLowerAsync_WithDateTime_UpdatesWhenLower()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            await cache.SetUnixTimeMillisecondsAsync("test", value);

            var lowerValue = value - TimeSpan.FromHours(1);
            long lowerUnixTimeValue = lowerValue.ToUnixTimeMilliseconds();

            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds,
                await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerUnixTimeValue, await cache.GetAsync<long>("test", 0));
        }
    }

    public virtual async Task SetIfLowerAsync_WithDateTime_DoesNotUpdateWhenHigher()
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

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value.AddHours(1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfLowerAsync_WithDateTime_InitializesWhenKeyNotExists()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            Assert.Equal(unixTimeValue, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfHigherAsync_WithDateTime_UpdatesWhenHigher()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            await cache.SetUnixTimeMillisecondsAsync("test", value);

            var higherValue = value + TimeSpan.FromHours(1);
            long higherUnixTimeValue = higherValue.ToUnixTimeMilliseconds();

            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds,
                await cache.SetIfHigherAsync("test", higherValue));
            Assert.Equal(higherUnixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(higherValue, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfHigherAsync_WithDateTime_DoesNotUpdateWhenLower()
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

            Assert.Equal(0, await cache.SetIfHigherAsync("test", value.AddHours(-1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfHigherAsync_WithDateTime_InitializesWhenKeyNotExists()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            Assert.Equal(unixTimeValue, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetAsync_WithLargeNumbersAndExpiration_PreservesValues()
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

    public virtual async Task ListAddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(String.Empty, 1));
        }
    }

    public virtual async Task ListAddAsync_WithNullValues_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(key, null as List<int>));
        }
    }

    public virtual async Task ListRemoveAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(String.Empty, 1));
        }
    }

    public virtual async Task ListRemoveAsync_WithNullValues_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(key, null as List<int>));
        }
    }

    public virtual async Task GetListAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(null));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(String.Empty));
        }
    }

    public virtual async Task ListAddAsync_WithDuplicates_StoresUniqueValuesOnly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);
        }
    }

    public virtual async Task ListRemoveAsync_WithMultipleValues_RemovesAll()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            await cache.ListRemoveAsync(key, [1, 2, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    public virtual async Task ListAddAsync_WithEmptyCollection_NoOp()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync<int>(key, []);

            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);
        }
    }

    public virtual async Task ListRemoveAsync_WithSingleValue_RemovesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);

            await cache.ListRemoveAsync(key, 2);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);

            await cache.ListRemoveAsync(key, 1);
            await cache.ListRemoveAsync(key, 3);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    public virtual async Task ListAddAsync_WithExistingNonListKey_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await cache.AddAsync("key1", 1);
                await cache.ListAddAsync("key1", 1);
            });
        }
    }

    public virtual async Task ListAddAsync_WithNullCollection_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(key, null as List<string>));
        }
    }

    public virtual async Task ListAddAsync_WithNullItem_IgnoresNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            Assert.Equal(0, await cache.ListAddAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListAddAsync(key, ["1", null]));
            var result = await cache.GetListAsync<string>(key);
            Assert.NotNull(result);
            Assert.Single(result.Value);
        }
    }

    public virtual async Task ListRemoveAsync_WithNullCollection_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(key, null as List<string>));
        }
    }

    public virtual async Task ListRemoveAsync_WithNullItem_IgnoresNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await cache.ListAddAsync(key, ["1"]);
            Assert.Equal(0, await cache.ListRemoveAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListRemoveAsync(key, ["1", null]));
            var result = await cache.GetListAsync<string>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    /// <summary>
    /// single strings don't get handled as char arrays
    /// </summary>
    public virtual async Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
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

    public virtual async Task GetListAsync_WithPaging_ReturnsCorrectPageSize()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:size";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var pagedResult = await cache.GetListAsync<int>(key, 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
        }
    }

    public virtual async Task GetListAsync_WithMultiplePages_ReturnsAllItems()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:multiple";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var actualResults = new HashSet<int>(values.Length);

            for (int page = 1; page < values.Length / 5 + 1; page++)
            {
                var pagedResult = await cache.GetListAsync<int>(key, page, 5);
                Assert.NotNull(pagedResult);
                Assert.Equal(5, pagedResult.Value.Count);
                actualResults.AddRange(pagedResult.Value);
            }

            Assert.Equal(values.Length, actualResults.Count);
        }
    }

    public virtual async Task GetListAsync_WithNewItemsAdded_ReturnsNewItemsLast()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:newitems";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var firstPageResults = new HashSet<int>(5);
            var firstResult = await cache.GetListAsync<int>(key, 1, 5);
            firstPageResults.AddRange(firstResult.Value);

            await cache.ListAddAsync(key, [21, 22], TimeSpan.FromMinutes(2));
            var lastPageResult = await cache.GetListAsync<int>(key, 5, 5);
            Assert.NotNull(lastPageResult);
            Assert.Equal(2, lastPageResult.Value.Count);

            var firstPageAgain = await cache.GetListAsync<int>(key, 1, 5);
            Assert.Equal(firstPageResults, firstPageAgain.Value.ToArray());
        }
    }

    public virtual async Task GetListAsync_WithInvalidPageNumber_ThrowsArgumentOutOfRangeException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:invalid";

            int[] values = [1, 2, 3, 4, 5];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetListAsync<int>(key, 0, 5));
        }
    }

    public virtual async Task GetListAsync_WithPageBeyondEnd_ReturnsEmptyCollection()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:beyond";

            int[] values = [1, 2, 3, 4, 5];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var pagedResult = await cache.GetListAsync<int>(key, 10, 5);
            Assert.NotNull(pagedResult);
            Assert.Empty(pagedResult.Value);
        }
    }

    public virtual async Task GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive()
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

    public virtual async Task ListAddAsync_WithPastExpiration_RemovesItem()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add:past";

            Assert.Equal(1, await cache.ListAddAsync(key, [1]));

            Assert.Equal(0, await cache.ListAddAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListAddAsync_WithFutureExpiration_AddsAndExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add:future";

            Assert.Equal(1, await cache.ListAddAsync(key, [2], TimeSpan.FromMilliseconds(100)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(2));

            await Task.Delay(150);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListAddAsync_WithMultipleExpirations_ExpiresIndividualItems()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add:multiple";

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

    public virtual async Task ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:remove:past";

            Assert.Equal(2, await cache.ListAddAsync(key, [1, 2]));

            Assert.Equal(1, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.Equal(0, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(2));

            // Expiration is not taken into account since it's a remove operation.
            Assert.Equal(1, await cache.ListRemoveAsync(key, [2], TimeSpan.FromSeconds(1)));
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task GetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAsync<string>(null));
        }
    }

    public virtual async Task GetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>(string.Empty));
        }
    }

    public virtual async Task GetAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>("   "));
        }
    }

    public virtual async Task SetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAsync<string>(null, "value"));
        }
    }

    public virtual async Task SetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAsync(string.Empty, "value"));
        }
    }

    public virtual async Task SetAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAsync("   ", "value"));
        }
    }

    public virtual async Task TryGetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAsync<string>(null));
        }
    }

    public virtual async Task TryGetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>(string.Empty));
        }
    }

    public virtual async Task TryGetAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>("   "));
        }
    }

    public virtual async Task AddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.AddAsync<string>(null, "value"));
        }
    }

    public virtual async Task AddAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.AddAsync(string.Empty, "value"));
        }
    }

    public virtual async Task AddAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.AddAsync("   ", "value"));
        }
    }

    public virtual async Task RemoveAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveAsync(null));
        }
    }

    public virtual async Task RemoveAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveAsync(string.Empty));
        }
    }

    public virtual async Task RemoveAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveAsync("   "));
        }
    }

    public virtual async Task GetAllAsync_WithNullKeys_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAllAsync<string>(null));
        }
    }

    public virtual async Task GetAllAsync_WithEmptyKeys_ReturnsEmpty()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var result = await cache.GetAllAsync<string>(Array.Empty<string>());
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }

    public virtual async Task GetAllAsync_WithKeysContainingNull_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllAsync<string>(new[] { "key1", null, "key2" }));
        }
    }

    public virtual async Task GetAllAsync_WithKeysContainingEmpty_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllAsync<string>(new[] { "key1", string.Empty, "key2" }));
        }
    }

    public virtual async Task GetAllAsync_WithKeysContainingWhitespace_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllAsync<string>(new[] { "key1", "   ", "key2" }));
        }
    }

    public virtual async Task SetAllAsync_WithNullItems_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAllAsync<string>(null));
        }
    }

    public virtual async Task SetAllAsync_WithEmptyItems_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            int result = await cache.SetAllAsync<string>(new Dictionary<string, string>());
            Assert.Equal(0, result);
        }
    }

    public virtual async Task SetAllAsync_WithItemsContainingNullKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var items = new Dictionary<string, string> { { "key1", "value1" }, { null, "value2" } };

            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllAsync(items));
        }
    }

    public virtual async Task SetAllAsync_WithItemsContainingEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var items = new Dictionary<string, string> { { "key1", "value1" }, { string.Empty, "value2" } };

            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllAsync(items));
        }
    }

    public virtual async Task SetAllAsync_WithItemsContainingWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var items = new Dictionary<string, string> { { "key1", "value1" }, { "   ", "value2" } };

            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllAsync(items));
        }
    }

    public virtual async Task RemoveAllAsync_WithNullKeys_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveAllAsync(null));
        }
    }

    public virtual async Task RemoveAllAsync_WithEmptyKeys_Succeeds()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync(Array.Empty<string>());
        }
    }

    public virtual async Task RemoveAllAsync_WithKeysContainingNull_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveAllAsync(new[] { "key1", null, "key2" }));
        }
    }

    public virtual async Task RemoveAllAsync_WithKeysContainingEmpty_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveAllAsync(new[] { "key1", string.Empty, "key2" }));
        }
    }

    public virtual async Task RemoveAllAsync_WithKeysContainingWhitespace_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveAllAsync(new[] { "key1", "   ", "key2" }));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithNullPrefix_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveByPrefixAsync(null));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithEmptyPrefix_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveByPrefixAsync(string.Empty));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithWhitespacePrefix_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveByPrefixAsync("   "));
        }
    }

    public virtual async Task IncrementAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IncrementAsync(null, 1));
        }
    }

    public virtual async Task IncrementAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.IncrementAsync(string.Empty, 1));
        }
    }

    public virtual async Task IncrementAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.IncrementAsync("   ", 1));
        }
    }

    public virtual async Task ReplaceAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.ReplaceAsync<int>(null, 1));
        }
    }

    public virtual async Task ReplaceAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ReplaceAsync<int>(string.Empty, 1));
        }
    }

    public virtual async Task ReplaceAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ReplaceAsync<int>("   ", 1));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ReplaceIfEqualAsync<string>(null, "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ReplaceIfEqualAsync(string.Empty, "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ReplaceIfEqualAsync("   ", "old", "new"));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.RemoveIfEqualAsync<string>(null, "value"));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveIfEqualAsync(string.Empty, "value"));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveIfEqualAsync("   ", "value"));
        }
    }

    public virtual async Task GetExpirationAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetExpirationAsync(null));
        }
    }

    public virtual async Task GetExpirationAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetExpirationAsync(string.Empty));
        }
    }

    public virtual async Task GetExpirationAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetExpirationAsync("   "));
        }
    }

    public virtual async Task SetExpirationAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.SetExpirationAsync(null, TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task SetExpirationAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.SetExpirationAsync(string.Empty, TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task SetExpirationAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.SetExpirationAsync("   ", TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task GetListAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetListAsync<string>(string.Empty));
        }
    }

    public virtual async Task GetListAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetListAsync<string>("   "));
        }
    }

    public virtual async Task ListAddAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ListAddAsync(string.Empty, "value"));
        }
    }

    public virtual async Task ListAddAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ListAddAsync("   ", "value"));
        }
    }

    public virtual async Task ListRemoveAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ListRemoveAsync(string.Empty, "value"));
        }
    }

    public virtual async Task ListRemoveAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ListRemoveAsync("   ", "value"));
        }
    }

    public virtual async Task GetAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("userId", "lowercase");
            await cache.SetAsync("UserId", "titlecase");
            await cache.SetAsync("USERID", "uppercase");

            var lower = await cache.GetAsync<string>("userId");
            var title = await cache.GetAsync<string>("UserId");
            var upper = await cache.GetAsync<string>("USERID");

            Assert.Equal("lowercase", lower.Value);
            Assert.Equal("titlecase", title.Value);
            Assert.Equal("uppercase", upper.Value);
        }
    }

    public virtual async Task SetAsync_WithDifferentCasedKeys_CreatesDistinctEntries()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("productId", 100);
            await cache.SetAsync("ProductId", 200);
            await cache.SetAsync("PRODUCTID", 300);

            var lower = await cache.GetAsync<int>("productId");
            var title = await cache.GetAsync<int>("ProductId");
            var upper = await cache.GetAsync<int>("PRODUCTID");

            Assert.Equal(100, lower.Value);
            Assert.Equal(200, title.Value);
            Assert.Equal(300, upper.Value);
        }
    }

    public virtual async Task RemoveAsync_WithSpecificCase_RemovesOnlyMatchingKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("sessionId", "session1");
            await cache.SetAsync("SessionId", "session2");
            await cache.SetAsync("SESSIONID", "session3");

            await cache.RemoveAsync("SessionId");

            var lower = await cache.GetAsync<string>("sessionId");
            var title = await cache.GetAsync<string>("SessionId");
            var upper = await cache.GetAsync<string>("SESSIONID");

            Assert.True(lower.HasValue);
            Assert.Equal("session1", lower.Value);
            Assert.False(title.HasValue);
            Assert.True(upper.HasValue);
            Assert.Equal("session3", upper.Value);
        }
    }

    public virtual async Task ExistsAsync_WithDifferentCasedKeys_ChecksExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("orderId", "order123");

            var lowerExists = await cache.ExistsAsync("orderId");
            var titleExists = await cache.ExistsAsync("OrderId");
            var upperExists = await cache.ExistsAsync("ORDERID");

            Assert.True(lowerExists);
            Assert.False(titleExists);
            Assert.False(upperExists);
        }
    }

    public virtual async Task GetAllAsync_WithMixedCaseKeys_RetrievesExactMatches()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("configKey", "value1");
            await cache.SetAsync("ConfigKey", "value2");
            await cache.SetAsync("CONFIGKEY", "value3");

            var results = await cache.GetAllAsync<string>(new[] { "configKey", "ConfigKey", "CONFIGKEY" });

            Assert.Equal(3, results.Count);
            Assert.Equal("value1", results["configKey"].Value);
            Assert.Equal("value2", results["ConfigKey"].Value);
            Assert.Equal("value3", results["CONFIGKEY"].Value);
        }
    }

    public virtual async Task SetAllAsync_WithDifferentCasedKeys_CreatesDistinctEntries()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var items = new Dictionary<string, int> { { "itemId", 1 }, { "ItemId", 2 }, { "ITEMID", 3 } };

            await cache.SetAllAsync(items);

            var results = await cache.GetAllAsync<int>(new[] { "itemId", "ItemId", "ITEMID" });

            Assert.Equal(3, results.Count);
            Assert.Equal(1, results["itemId"].Value);
            Assert.Equal(2, results["ItemId"].Value);
            Assert.Equal(3, results["ITEMID"].Value);
        }
    }

    public virtual async Task RemoveAllAsync_WithMixedCaseKeys_RemovesOnlyExactMatches()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("cacheKey", "val1");
            await cache.SetAsync("CacheKey", "val2");
            await cache.SetAsync("CACHEKEY", "val3");

            await cache.RemoveAllAsync(new[] { "CacheKey" });

            var lower = await cache.GetAsync<string>("cacheKey");
            var title = await cache.GetAsync<string>("CacheKey");
            var upper = await cache.GetAsync<string>("CACHEKEY");

            Assert.True(lower.HasValue);
            Assert.False(title.HasValue);
            Assert.True(upper.HasValue);
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithCaseSensitivePrefix_RemovesOnlyMatchingCase()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("user:123", "data1");
            await cache.SetAsync("User:456", "data2");
            await cache.SetAsync("USER:789", "data3");
            await cache.SetAsync("user:abc", "data4");

            await cache.RemoveByPrefixAsync("user:");

            var lower1 = await cache.ExistsAsync("user:123");
            var lower2 = await cache.ExistsAsync("user:abc");
            var title = await cache.ExistsAsync("User:456");
            var upper = await cache.ExistsAsync("USER:789");

            Assert.False(lower1);
            Assert.False(lower2);
            Assert.True(title);
            Assert.True(upper);
        }
    }

    public virtual async Task IncrementAsync_WithDifferentCasedKeys_IncrementsDistinctCounters()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var lower = await cache.IncrementAsync("counter", 1);
            var title = await cache.IncrementAsync("Counter", 2);
            var upper = await cache.IncrementAsync("COUNTER", 3);

            Assert.Equal(1, lower);
            Assert.Equal(2, title);
            Assert.Equal(3, upper);

            var lowerFinal = await cache.GetAsync<long>("counter");
            var titleFinal = await cache.GetAsync<long>("Counter");
            var upperFinal = await cache.GetAsync<long>("COUNTER");

            Assert.Equal(1, lowerFinal.Value);
            Assert.Equal(2, titleFinal.Value);
            Assert.Equal(3, upperFinal.Value);
        }
    }

    public virtual async Task ReplaceAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("TEST", 1);
            await cache.SetAsync("test", 2);

            Assert.True(await cache.ReplaceAsync("TEST", 10));
            Assert.Equal(10, (await cache.GetAsync<int>("TEST")).Value);
            Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);

            Assert.True(await cache.ReplaceAsync("test", 20));
            Assert.Equal(10, (await cache.GetAsync<int>("TEST")).Value);
            Assert.Equal(20, (await cache.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithDifferentCasedKeys_ReplacesOnlyExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("statusCode", 200);
            await cache.SetAsync("StatusCode", 201);
            await cache.SetAsync("STATUSCODE", 202);

            var replaced = await cache.ReplaceIfEqualAsync("StatusCode", 201, 299);

            Assert.True(replaced);

            var lower = await cache.GetAsync<int>("statusCode");
            var title = await cache.GetAsync<int>("StatusCode");
            var upper = await cache.GetAsync<int>("STATUSCODE");

            Assert.Equal(200, lower.Value);
            Assert.Equal(299, title.Value);
            Assert.Equal(202, upper.Value);
        }
    }

    public virtual async Task GetExpirationAsync_WithDifferentCasedKeys_GetsExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var expiration1 = DateTime.UtcNow.AddMinutes(10);
            var expiration2 = DateTime.UtcNow.AddMinutes(20);
            var expiration3 = DateTime.UtcNow.AddMinutes(30);

            await cache.SetAsync("tokenId", "token1", expiration1);
            await cache.SetAsync("TokenId", "token2", expiration2);
            await cache.SetAsync("TOKENID", "token3", expiration3);

            var lowerExp = await cache.GetExpirationAsync("tokenId");
            var titleExp = await cache.GetExpirationAsync("TokenId");
            var upperExp = await cache.GetExpirationAsync("TOKENID");

            Assert.NotNull(lowerExp);
            Assert.NotNull(titleExp);
            Assert.NotNull(upperExp);

            var actualExpiration1 = DateTime.UtcNow.Add(lowerExp.Value);
            var actualExpiration2 = DateTime.UtcNow.Add(titleExp.Value);
            var actualExpiration3 = DateTime.UtcNow.Add(upperExp.Value);
            Assert.True((actualExpiration1 - expiration1).TotalSeconds < 2);
            Assert.True((actualExpiration2 - expiration2).TotalSeconds < 2);
            Assert.True((actualExpiration3 - expiration3).TotalSeconds < 2);
        }
    }

    public virtual async Task SetExpirationAsync_WithDifferentCasedKeys_SetsOnlyExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("apiKey", "key1");
            await cache.SetAsync("ApiKey", "key2");
            await cache.SetAsync("APIKEY", "key3");

            var newExpiration = DateTime.UtcNow.AddMinutes(5);
            await cache.SetExpirationAsync("ApiKey", newExpiration);

            var lowerExp = await cache.GetExpirationAsync("apiKey");
            var titleExp = await cache.GetExpirationAsync("ApiKey");
            var upperExp = await cache.GetExpirationAsync("APIKEY");

            Assert.Null(lowerExp);
            Assert.NotNull(titleExp);
            Assert.Null(upperExp);

            var actualExpiration = DateTime.UtcNow.Add(titleExp.Value);
            Assert.True((actualExpiration - newExpiration).TotalSeconds < 2);
        }
    }

    public virtual async Task ListAddAsync_WithDifferentCasedKeys_MaintainsDistinctLists()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.ListAddAsync("queueName", "item1");
            await cache.ListAddAsync("QueueName", "item2");
            await cache.ListAddAsync("QUEUENAME", "item3");

            var lowerList = await cache.GetListAsync<string>("queueName");
            var titleList = await cache.GetListAsync<string>("QueueName");
            var upperList = await cache.GetListAsync<string>("QUEUENAME");

            Assert.Single(lowerList.Value);
            Assert.Contains("item1", lowerList.Value);

            Assert.Single(titleList.Value);
            Assert.Contains("item2", titleList.Value);

            Assert.Single(upperList.Value);
            Assert.Contains("item3", upperList.Value);
        }
    }

    public virtual async Task SetAsync_WithDifferentCasedScopes_MaintainsDistinctNamespaces()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var scopedLower = new ScopedCacheClient(cache, "tenant");
            var scopedTitle = new ScopedCacheClient(cache, "Tenant");
            var scopedUpper = new ScopedCacheClient(cache, "TENANT");

            await scopedLower.SetAsync("dataId", "lower");
            await scopedTitle.SetAsync("dataId", "title");
            await scopedUpper.SetAsync("dataId", "upper");

            var lowerVal = await scopedLower.GetAsync<string>("dataId");
            var titleVal = await scopedTitle.GetAsync<string>("dataId");
            var upperVal = await scopedUpper.GetAsync<string>("dataId");

            Assert.Equal("lower", lowerVal.Value);
            Assert.Equal("title", titleVal.Value);
            Assert.Equal("upper", upperVal.Value);
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithDifferentCasedScopes_RemovesOnlyMatchingScope()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var scopedLower = new ScopedCacheClient(cache, "project");
            var scopedTitle = new ScopedCacheClient(cache, "Project");

            await scopedLower.SetAsync("settingA", "valueA");
            await scopedLower.SetAsync("settingB", "valueB");
            await scopedTitle.SetAsync("settingA", "valueC");
            await scopedTitle.SetAsync("settingB", "valueD");

            await scopedLower.RemoveByPrefixAsync("setting");

            var lowerA = await scopedLower.GetAsync<string>("settingA");
            var lowerB = await scopedLower.GetAsync<string>("settingB");
            var titleA = await scopedTitle.GetAsync<string>("settingA");
            var titleB = await scopedTitle.GetAsync<string>("settingB");

            Assert.False(lowerA.HasValue);
            Assert.False(lowerB.HasValue);
            Assert.True(titleA.HasValue);
            Assert.True(titleB.HasValue);
        }
    }

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
