using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Caching;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Caching;

public class CacheListTests : TestWithLoggingBase
{
    private static readonly DateTime _utcNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public CacheListTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public void Apply_WithExpiredMembers_PrunesThemAndReportsTheCount()
    {
        // Arrange
        var list = CacheList<int>.Create(new Dictionary<int, DateTime?> { [1] = _utcNow.AddMinutes(-1), [2] = null });

        // Act
        long count = list.Apply([3], null, _utcNow, remove: false, out bool changed, out int expired);
        list.Commit();

        // Assert
        Assert.Equal(1, count);
        Assert.True(changed);
        Assert.Equal(1, expired);
        Assert.Equal([2, 3], list.Values.Keys.Order());
        Assert.Null(list.ExpiresAt);
    }

    [Fact]
    public void CacheList_IsNotPartOfThePublicApi()
    {
        // Arrange & Act
        var types = new[] { typeof(CacheList<>), typeof(ICacheList) };

        // Assert
        Assert.All(types, type => Assert.False(type.IsVisible, $"{type.Name} must stay internal"));
    }

    [Fact]
    public void CanUpdateInPlace_WithCallbackFreeComparerAndNoUpdateInProgress_ReturnsTrue()
    {
        // Arrange
        var list = CacheList<int>.Create([1], null);
        bool beforeUpdate = list.CanUpdateInPlace;

        // Act
        list.Apply([2], null, _utcNow, remove: false, out _, out _);
        bool duringUpdate = list.CanUpdateInPlace;
        list.Commit();

        // Assert
        Assert.True(beforeUpdate);
        Assert.False(duringUpdate);
        Assert.True(list.CanUpdateInPlace);
    }

    [Fact]
    public void CanUpdateInPlace_WithCustomComparerOrAdoptedNonDictionary_ReturnsFalse()
    {
        // Arrange & Act
        var caseInsensitive = CacheList<string>.Adopt(new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase) { ["a"] = null });
        var sorted = CacheList<int>.Adopt(new SortedDictionary<int, DateTime?> { [1] = null });

        // Assert
        Assert.False(caseInsensitive.CanUpdateInPlace);
        Assert.False(sorted.CanUpdateInPlace);
    }

    [Fact]
    public void CreateCandidate_DuringAnUpdate_LeavesOutTheUncommittedChange()
    {
        // Arrange
        var list = CacheList<int>.Create([1], null);
        list.Apply([2], null, _utcNow, remove: false, out _, out _);

        // Act
        var candidate = list.CreateCandidate();
        list.Rollback();

        // Assert
        Assert.NotSame(list.Values, candidate.Values);
        Assert.Equal([1], candidate.Values.Keys);
        Assert.Equal([1], list.Values.Keys);
    }

    [Fact]
    public void Read_WithPageAndExpiredMembers_ReturnsOnlyLiveMembers()
    {
        // Arrange
        var list = CacheList<int>.Create(new Dictionary<int, DateTime?> { [1] = null, [2] = _utcNow.AddMinutes(-1), [3] = null, [4] = null });

        // Act
        var all = list.Read(_utcNow, page: null, pageSize: 100);
        var first = list.Read(_utcNow, page: 1, pageSize: 2);
        var second = list.Read(_utcNow, page: 2, pageSize: 2);

        // Assert
        Assert.Equal([1, 3, 4], all.Value.Order());
        Assert.Equal(2, first.Value.Count);
        Assert.Equal([1, 3, 4], first.Value.Concat(second.Value).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_AfterAnUpdate_RestoresMembersAndExpiration(bool remove)
    {
        // Arrange
        var list = CacheList<int>.Create([1, 2], _utcNow.AddMinutes(5));
        list.Apply(remove ? [1] : [3], null, _utcNow, remove, out bool changed, out _);

        // Act
        list.Rollback();

        // Assert
        Assert.True(changed);
        Assert.Equal([1, 2], list.Values.Keys.Order());
        Assert.Equal(_utcNow.AddMinutes(5), list.ExpiresAt);
        Assert.True(list.CanUpdateInPlace);
    }
}
