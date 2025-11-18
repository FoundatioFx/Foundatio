using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
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
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveByPrefixAsync(String.Empty));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithWhitespacePrefix_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveByPrefixAsync("   "));
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

            bool lower1 = await cache.ExistsAsync("user:123");
            bool lower2 = await cache.ExistsAsync("user:abc");
            bool title = await cache.ExistsAsync("User:456");
            bool upper = await cache.ExistsAsync("USER:789");

            Assert.False(lower1);
            Assert.False(lower2);
            Assert.True(title);
            Assert.True(upper);
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
}
