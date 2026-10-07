using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public class InMemoryListSnapshotTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_OnDictionaryBackedSnapshot_AppliesSingleItemChange(bool remove)
    {
        // Arrange
        var snapshot = new ListSnapshot<int>(CreateValues(1000));
        int item = remove ? 500 : -1;

        // Act
        var updated = snapshot.Update([item], null, Now, remove, out long removed);

        // Assert
        var expected = Enumerable.Range(0, 1000).Where(i => i != 500 || !remove).ToList();
        if (!remove)
            expected.Add(-1);
        Assert.Equal(remove ? 1 : 0, removed);
        Assert.Equal(expected.Count, updated.Count);
        Assert.Equal(expected.Order(), updated.Read(Now).Order());
        Assert.False(updated.IsIndexed);

        var second = updated.Update([-2], null, Now, remove: false, out _);
        Assert.True(second.IsIndexed);
        Assert.Equal(expected.Append(-2).Order(), second.Read(Now).Order());
    }

    [Fact]
    public void Update_WhenGrowingPastPartitionCount_KeepsAllValues()
    {
        // Arrange
        var snapshot = new ListSnapshot<int>(CreateValues(64));

        // Act
        for (int i = 64; i < 2064; i++)
            snapshot = snapshot.Update([i], null, Now, remove: false, out _);
        for (int i = 0; i < 2064; i += 2)
            snapshot = snapshot.Update([i], null, Now, remove: true, out _);

        // Assert
        var expected = Enumerable.Range(0, 2064).Where(i => i % 2 == 1).ToArray();
        Assert.Equal(expected.Length, snapshot.Count);
        Assert.Equal(expected, snapshot.Read(Now).Order());
        Assert.All(expected, i => Assert.True(snapshot.AsReadOnlyDictionary().ContainsKey(i)));
        Assert.False(snapshot.AsReadOnlyDictionary().ContainsKey(0));
    }

    [Fact]
    public void ExpiresAt_WithMixedPermanentAndExpiringItems_ReturnsLatestOrNull()
    {
        // Arrange
        var values = CreateValues(100, i => Now.AddMinutes(1 + i % 3));
        values[1000] = null;
        var snapshot = new ListSnapshot<int>(values);

        // Act
        var withoutPermanent = snapshot.Update([1000], null, Now, remove: true, out _);
        var withoutLatest = withoutPermanent.Update(values.Where(kvp => kvp.Value == Now.AddMinutes(3)).Select(kvp => kvp.Key).ToArray(), null, Now, remove: true, out _);
        var singleChange = withoutLatest.Update([2000], Now.AddMinutes(10), Now, remove: false, out _);

        // Assert
        Assert.Null(snapshot.ExpiresAt);
        Assert.Equal(Now.AddMinutes(3), withoutPermanent.ExpiresAt);
        Assert.Equal(Now.AddMinutes(2), withoutLatest.ExpiresAt);
        Assert.Equal(Now.AddMinutes(10), singleChange.ExpiresAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_WithExpiredItems_PrunesThem(bool indexed)
    {
        // Arrange
        var values = CreateValues(1000, i => i < 100 ? Now.AddMinutes(-1) : null);
        var snapshot = new ListSnapshot<int>(values);
        if (indexed)
            snapshot = Indexed(snapshot, Now.AddMinutes(-2));

        // Act
        var updated = snapshot.Update([-1], null, Now, remove: false, out _);

        // Assert
        var expected = Enumerable.Range(100, 900).Append(-1).Concat(indexed ? [5000] : []).Order().ToArray();
        Assert.Equal(expected.Length, updated.Count);
        Assert.Equal(expected, updated.Read(Now).Order());
        Assert.False(updated.AsReadOnlyDictionary().ContainsKey(0));
    }

    [Theory]
    [InlineData("single-add")]
    [InlineData("single-remove")]
    [InlineData("batch-add")]
    [InlineData("batch-remove")]
    [InlineData("prune")]
    public void Update_DoesNotMutateSourceSnapshot(string operation)
    {
        // Arrange
        var values = CreateValues(1000, i => i < 10 ? Now.AddMinutes(-1) : null);
        foreach (bool indexed in new[] { false, true })
        {
            var source = new ListSnapshot<int>(values);
            if (indexed)
                source = Indexed(source, Now.AddMinutes(-2));
            var before = source.Read(Now.AddMinutes(-2)).Order().ToArray();
            var expiresBefore = source.ExpiresAt;

            // Act
            _ = operation switch
            {
                "single-add" => source.Update([-1], null, Now.AddMinutes(-2), remove: false, out _),
                "single-remove" => source.Update([500], null, Now.AddMinutes(-2), remove: true, out _),
                "batch-add" => source.Update(Enumerable.Range(2000, 600).ToArray(), null, Now.AddMinutes(-2), remove: false, out _),
                "batch-remove" => source.Update(Enumerable.Range(0, 600).ToArray(), null, Now.AddMinutes(-2), remove: true, out _),
                _ => source.Update([-1], null, Now, remove: false, out _)
            };

            // Assert
            Assert.Equal(before, source.Read(Now.AddMinutes(-2)).Order());
            Assert.Equal(before.Length, source.Count);
            Assert.Equal(expiresBefore, source.ExpiresAt);
            Assert.Equal(before.Length, source.AsReadOnlyDictionary().Count);
        }
    }

    [Fact]
    public void Update_WithRandomOperations_MatchesDictionaryModel()
    {
        // Arrange
        var random = new Random(570);
        var model = CreateValues(200);
        var snapshot = new ListSnapshot<int>(new Dictionary<int, DateTime?>(model));
        var utcNow = Now;

        for (int step = 0; step < 3000; step++)
        {
            // Act
            utcNow = utcNow.AddSeconds(random.Next(0, 3));
            int batchSize = random.Next(10) == 0 ? random.Next(20, 400) : random.Next(1, 3);
            var items = Enumerable.Range(0, batchSize).Select(_ => random.Next(0, 1500)).Distinct().ToArray();
            bool remove = random.Next(2) == 0;
            DateTime? expiration = random.Next(4) == 0 ? utcNow.AddSeconds(random.Next(1, 30)) : null;
            snapshot = snapshot.Update(items, remove ? null : expiration, utcNow, remove, out long removed);

            foreach (int key in model.Where(kvp => kvp.Value < utcNow).Select(kvp => kvp.Key).ToArray())
                model.Remove(key);
            long expectedRemoved = 0;
            foreach (int item in items)
            {
                if (remove)
                    expectedRemoved += model.Remove(item) ? 1 : 0;
                else
                    model[item] = expiration;
            }

            // Assert
            Assert.Equal(expectedRemoved, removed);
            Assert.Equal(model.Count, snapshot.Count);
            Assert.Equal(model.Keys.Order(), snapshot.Read(utcNow).Order());
            Assert.Equal(model.Values.Contains(null) || model.Count == 0 ? null : model.Values.Max(), snapshot.ExpiresAt);
            var view = snapshot.AsReadOnlyDictionary();
            Assert.Equal(model.Count, view.Count);
            Assert.Equal(model.OrderBy(kvp => kvp.Key), view.OrderBy(kvp => kvp.Key));
        }
    }

    [Fact]
    public void AsReadOnlyDictionary_ExposesCurrentItemsAndRejectsChanges()
    {
        // Arrange
        var values = CreateValues(100, i => i == 7 ? Now.AddMinutes(5) : null);
        var snapshot = Indexed(new ListSnapshot<int>(values), Now);

        // Act
        var view = snapshot.AsReadOnlyDictionary();

        // Assert
        Assert.Equal(101, view.Count);
        Assert.Equal(101, ((ICollection)view).Count);
        Assert.True(view.IsReadOnly);
        Assert.True(view.ContainsKey(5000));
        Assert.True(view.TryGetValue(7, out var expiration));
        Assert.Equal(Now.AddMinutes(5), expiration);
        Assert.Null(view[0]);
        Assert.False(view.TryGetValue(-1, out _));
        Assert.Throws<KeyNotFoundException>(() => view[-1]);
        Assert.Equal(Enumerable.Range(0, 100).Append(5000).Order(), view.Keys.Order());
        Assert.Equal(101, view.Values.Count);
        Assert.Contains(new KeyValuePair<int, DateTime?>(7, Now.AddMinutes(5)), view);
        Assert.Throws<NotSupportedException>(() => view.Add(-1, null));
        Assert.Throws<NotSupportedException>(() => view.Remove(0));
        Assert.Throws<NotSupportedException>(() => view[0] = Now);
        Assert.Throws<NotSupportedException>(view.Clear);
    }

    /// <summary>Adds item 5000 and makes the next small change build the index, the way repeated small writes do.</summary>
    private static ListSnapshot<int> Indexed(ListSnapshot<int> snapshot, DateTime utcNow)
    {
        var indexed = snapshot.Update([5000], null, utcNow, remove: false, out _).Update([5000], null, utcNow, remove: false, out _);
        Assert.True(indexed.IsIndexed);
        return indexed;
    }

    private static Dictionary<int, DateTime?> CreateValues(int count, Func<int, DateTime?>? expiration = null)
    {
        return Enumerable.Range(0, count).ToDictionary(i => i, i => expiration?.Invoke(i));
    }
}
