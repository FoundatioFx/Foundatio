using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Caching;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching;

public class RedisCacheClientTests : CacheClientTestsBase
{
    public RedisCacheClientTests(ITestOutputHelper output) : base(output)
    {
        var muxer = SharedConnection.GetMuxer(Log);
        muxer.FlushAllAsync().GetAwaiter().GetResult();
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new RedisCacheClient(o => o.ConnectionMultiplexer(SharedConnection.GetMuxer(Log)).LoggerFactory(Log).ShouldThrowOnSerializationError(shouldThrowOnSerializationError));
    }

    [Fact]
    public override Task CanGetAllAsync()
    {
        return base.CanGetAllAsync();
    }

    [Fact]
    public override Task CanGetAllWithOverlapAsync()
    {
        return base.CanGetAllWithOverlapAsync();
    }

    [Fact]
    public override Task CanSetAsync()
    {
        return base.CanSetAsync();
    }

    [Fact]
    public override Task CanSetAndGetValueAsync()
    {
        return base.CanSetAndGetValueAsync();
    }

    [Fact]
    public override Task CanAddAsync()
    {
        return base.CanAddAsync();
    }

    [Fact]
    public override Task CanAddConcurrentlyAsync()
    {
        return base.CanAddConcurrentlyAsync();
    }

    [Fact]
    public override Task CanGetAsync()
    {
        return base.CanGetAsync();
    }

    [Fact]
    public override Task CanTryGetAsync()
    {
        return base.CanTryGetAsync();
    }

    [Fact]
    public override Task CanUseScopedCachesAsync()
    {
        return base.CanUseScopedCachesAsync();
    }

    [Fact]
    public override Task CanSetAndGetObjectAsync()
    {
        return base.CanSetAndGetObjectAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixAsync()
    {
        return base.CanRemoveByPrefixAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixWithScopedCachesAsync()
    {
        return base.CanRemoveByPrefixWithScopedCachesAsync();
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(5000)]
    [InlineData(50000)]
    public override Task CanRemoveByPrefixMultipleEntriesAsync(int count)
    {
        return base.CanRemoveByPrefixMultipleEntriesAsync(count);
    }

    [Fact]
    public override Task CanSetExpirationAsync()
    {
        return base.CanSetExpirationAsync();
    }

    [Fact]
    public override Task CanIncrementAsync()
    {
        return base.CanIncrementAsync();
    }

    [Fact]
    public override Task CanIncrementAndExpireAsync()
    {
        return base.CanIncrementAndExpireAsync();
    }

    [Fact]
    public override Task CanGetAndSetDateTimeAsync()
    {
        return base.CanGetAndSetDateTimeAsync();
    }

    [Fact]
    public override Task CanRemoveIfEqual()
    {
        return base.CanRemoveIfEqual();
    }

    [Fact]
    public override Task CanReplaceIfEqual()
    {
        return base.CanReplaceIfEqual();
    }

    [Fact]
    public override Task CanRoundTripLargeNumbersAsync()
    {
        return base.CanRoundTripLargeNumbersAsync();
    }

    [Fact]
    public override Task CanRoundTripLargeNumbersWithExpirationAsync()
    {
        return base.CanRoundTripLargeNumbersWithExpirationAsync();
    }

    [Fact]
    public override Task CanManageListsAsync()
    {
        return base.CanManageListsAsync();
    }

    [Fact]
    public async Task CanUpgradeListType()
    {
        var db = SharedConnection.GetMuxer(Log).GetDatabase();
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            var items = new List<RedisValue>();
            for (int i = 1; i < 20001; i++)
                items.Add(Guid.NewGuid().ToString());

            await cache.RemoveAllAsync();

            await db.SetAddAsync("mylist", items.ToArray());
            await cache.ListAddAsync("mylist", new[] { "newitem1", "newitem2" });
            await cache.ListAddAsync("mylist", "newitem3");

            var listItems = await cache.GetListAsync<string>("mylist");
            Assert.Equal(items.Count + 3, listItems.Value.Count);

            await cache.RemoveAllAsync();

            await db.SetAddAsync("mylist", items.ToArray());
            await cache.ListRemoveAsync("mylist", (string)items[10]);

            listItems = await cache.GetListAsync<string>("mylist");
            Assert.Equal(items.Count - 1, listItems.Value.Count);
        }
    }

    [Fact]
    public async Task CanManageLargeListsAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var items = new List<string>();
            // test paging through items in list
            for (int i = 1; i < 20001; i++)
                items.Add(Guid.NewGuid().ToString());

            foreach (var batch in Batch(items, 1000))
                await cache.ListAddAsync("largelist", batch);

            var pagedResult = await cache.GetListAsync<string>("largelist", 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { items[0], items[1], items[2], items[3], items[4] });

            pagedResult = await cache.GetListAsync<string>("largelist", 2, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { items[5], items[6], items[7], items[8], items[9] });

            string newGuid1 = Guid.NewGuid().ToString();
            string newGuid2 = Guid.NewGuid().ToString();
            await cache.ListAddAsync("largelist", new[] { newGuid1, newGuid2 });

            int page = (20000 / 5) + 1;
            pagedResult = await cache.GetListAsync<string>("largelist", page, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(2, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { newGuid1, newGuid2 });

            long result = await cache.ListAddAsync("largelist", Guid.NewGuid().ToString());
            Assert.Equal(1, result);

            result = await cache.ListRemoveAsync("largelist", items[1]);
            Assert.Equal(1, result);

            pagedResult = await cache.GetListAsync<string>("largelist", 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { items[0], items[2], items[3], items[4], items[5] });
        }
    }

    [Fact(Skip = "Performance Test")]
    public override Task MeasureThroughputAsync()
    {
        return base.MeasureThroughputAsync();
    }

    [Fact(Skip = "Performance Test")]
    public override Task MeasureSerializerSimpleThroughputAsync()
    {
        return base.MeasureSerializerSimpleThroughputAsync();
    }

    [Fact(Skip = "Performance Test")]
    public override Task MeasureSerializerComplexThroughputAsync()
    {
        return base.MeasureSerializerComplexThroughputAsync();
    }

    private IEnumerable<IEnumerable<TSource>> Batch<TSource>(IList<TSource> source, int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var enumerator = source.GetEnumerator();
        for (int i = 0; i < source.Count; i += size)
        {
            enumerator.MoveNext();
            yield return GetChunk(i, Math.Min(i + size, source.Count));
        }
        IEnumerable<TSource> GetChunk(int from, int toExclusive)
        {
            for (int j = from; j < toExclusive; j++)
            {
                enumerator.MoveNext();
                yield return source[j];
            }
        }
    }
}
