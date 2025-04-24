using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Redis.Extensions;

internal static class EnumerableExtensions
{
    public static IEnumerable<T[]> Batch<T>(
        this IEnumerable<T> source,
        int batchSize)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        using var e = source.GetEnumerator();
        while (true)
        {
            // allocate one buffer for this batch
            var buffer = new T[batchSize];
            int count = 0;

            while (count < batchSize && e.MoveNext())
                buffer[count++] = e.Current;

            if (count == 0)
                yield break;

            if (count == batchSize)
            {
                yield return buffer;
            }
            else
            {
                // final partial batch: copy to a right-sized array
                var tail = new T[count];
                Array.Copy(buffer, tail, count);
                yield return tail;
                yield break;
            }
        }
    }

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
