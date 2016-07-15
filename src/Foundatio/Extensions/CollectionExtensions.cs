using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Extensions {
    internal static class CollectionExtensions {
        public static ICollection<T> ReduceTimeSeries<T>(this ICollection<T> items, Func<T, DateTime> dateSelector, Func<ICollection<T>, DateTime, T> reducer, int dataPoints) {
            if (items.Count <= dataPoints)
                return items;

            var minTicks = items.Min(dateSelector).Ticks;
            var maxTicks = items.Max(dateSelector).Ticks;

            var bucketSize = (maxTicks - minTicks) / dataPoints;
            var buckets = new List<long>();
            long currentTick = minTicks;
            while (currentTick < maxTicks) {
                buckets.Add(currentTick);
                currentTick += bucketSize;
            }

            buckets.Reverse();

            return items.GroupBy(i => buckets.First(b => dateSelector(i).Ticks >= b)).Select(g => reducer(g.ToList(), new DateTime(g.Key))).ToList();
        }
    }
}
