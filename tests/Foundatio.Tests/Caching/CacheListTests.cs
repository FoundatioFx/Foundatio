using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public class CacheListTests
{
    private static readonly DateTime UtcNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_AfterAnUpdate_RestoresMembersAndExpiration(bool remove)
    {
        var list = CacheList<int>.Create([1, 2], UtcNow.AddMinutes(5));

        list.Apply(remove ? [1] : [3], null, UtcNow, remove, out bool changed, out _);
        list.Rollback();

        Assert.True(changed);
        Assert.Equal([1, 2], list.Values.Keys.Order());
        Assert.Equal(UtcNow.AddMinutes(5), list.ExpiresAt);
        Assert.True(list.CanUpdateInPlace);
    }

    [Fact]
    public void Apply_WithExpiredMembers_PrunesThemAndReportsTheCount()
    {
        var list = CacheList<int>.Create(new Dictionary<int, DateTime?> { [1] = UtcNow.AddMinutes(-1), [2] = null });

        long count = list.Apply([3], null, UtcNow, remove: false, out bool changed, out int expired);
        list.Commit();

        Assert.Equal(1, count);
        Assert.True(changed);
        Assert.Equal(1, expired);
        Assert.Equal([2, 3], list.Values.Keys.Order());
        Assert.Null(list.ExpiresAt);
    }

    [Fact]
    public void CreateCandidate_DuringAnUpdate_LeavesOutTheUncommittedChange()
    {
        var list = CacheList<int>.Create([1], null);
        list.Apply([2], null, UtcNow, remove: false, out _, out _);

        var candidate = list.CreateCandidate();
        list.Rollback();

        Assert.NotSame(list.Values, candidate.Values);
        Assert.Equal([1], candidate.Values.Keys);
        Assert.Equal([1], list.Values.Keys);
    }

    [Fact]
    public void CanUpdateInPlace_WithCallbackFreeComparerAndNoUpdateInProgress_ReturnsTrue()
    {
        var list = CacheList<int>.Create([1], null);
        Assert.True(list.CanUpdateInPlace);

        list.Apply([2], null, UtcNow, remove: false, out _, out _);
        Assert.False(list.CanUpdateInPlace);

        list.Commit();
        Assert.True(list.CanUpdateInPlace);
    }

    [Fact]
    public void CanUpdateInPlace_WithCustomComparerOrAdoptedNonDictionary_ReturnsFalse()
    {
        var caseInsensitive = CacheList<string>.Adopt(new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase) { ["a"] = null });
        var sorted = CacheList<int>.Adopt(new SortedDictionary<int, DateTime?> { [1] = null });

        Assert.False(caseInsensitive.CanUpdateInPlace);
        Assert.False(sorted.CanUpdateInPlace);
    }

    [Fact]
    public void Read_WithPageAndExpiredMembers_ReturnsOnlyLiveMembers()
    {
        var list = CacheList<int>.Create(new Dictionary<int, DateTime?> { [1] = null, [2] = UtcNow.AddMinutes(-1), [3] = null, [4] = null });

        var all = list.Read(UtcNow, page: null, pageSize: 100);
        var first = list.Read(UtcNow, page: 1, pageSize: 2);
        var second = list.Read(UtcNow, page: 2, pageSize: 2);

        Assert.Equal([1, 3, 4], all.Value.Order());
        Assert.Equal(2, first.Value.Count);
        Assert.Equal([1, 3, 4], first.Value.Concat(second.Value).Order());
    }
}
