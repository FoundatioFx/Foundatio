using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Extensions;

internal static class DictionaryExtensions
{
    /// <summary>
    /// Streams a dictionary to <paramref name="handler"/> in fixed‑size batches
    /// with a single rented buffer (constant memory).
    /// </summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="source">The dictionary to process.</param>
    /// <param name="batchSize">Number of items per batch (≥ 1).</param>
    /// <param name="handler">
    /// Async callback that consumes a <see cref="ReadOnlyMemory{T}"/> slice
    /// representing one full or partial batch of <see cref="KeyValuePair{TKey,TValue}"/>.
    /// </param>
    /// <returns>Total item count streamed.</returns>
    public static async Task<int> BatchAsync<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> source,
        int batchSize,
        Func<ReadOnlyMemory<KeyValuePair<TKey, TValue>>, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Fast path for very small dictionaries
        if (source.Count == 0)
            return 0;

        if (batchSize >= source.Count)
        {
            var oneShot = source.ToArray();
            await handler(oneShot.AsMemory()).AnyContext();
            return oneShot.Length;
        }

        var buffer = ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(batchSize);
        int filled = 0, total = 0;

        try
        {
            foreach (var kvp in source)
            {
                buffer[filled++] = kvp;

                if (filled == batchSize)
                {
                    await handler(buffer.AsMemory(0, filled)).AnyContext();
                    total += filled;
                    filled = 0;
                }
            }

            if (filled > 0)
            {
                await handler(buffer.AsMemory(0, filled)).AnyContext();
                total += filled;
            }
        }
        finally
        {
            ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Return(buffer, clearArray: true);
        }

        return total;
    }
}
