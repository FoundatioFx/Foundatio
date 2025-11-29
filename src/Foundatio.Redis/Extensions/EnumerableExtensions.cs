using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Redis.Extensions;

internal static class EnumerableExtensions
{
    public static async IAsyncEnumerable<T[]> BatchAsync<T>(
        this IAsyncEnumerable<T> source,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var bucket = new List<T>(batchSize);
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            bucket.Add(item);
            if (bucket.Count >= batchSize)
            {
                yield return bucket.ToArray();
                bucket.Clear();
            }
        }

        if (bucket.Count > 0)
            yield return bucket.ToArray();
    }
}
