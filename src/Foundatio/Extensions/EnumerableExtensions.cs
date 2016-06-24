using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Foundatio.Extensions {
    public static class EnumerableExtensions {
        public static void ForEach<T>(this IEnumerable<T> collection, Action<T> action) {
            if (collection == null || action == null)
                return;

            foreach (var item in collection)
                action(item);
        }

        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> range) {
            foreach (var r in range)
                list.Add(r);
        }

        public static IList<TR> FullOuterGroupJoin<TA, TB, TK, TR>(
            this IEnumerable<TA> a,
            IEnumerable<TB> b,
            Func<TA, TK> selectKeyA,
            Func<TB, TK> selectKeyB,
            Func<IEnumerable<TA>, IEnumerable<TB>, TK, TR> projection,
            IEqualityComparer<TK> cmp = null) {
            cmp = cmp ?? EqualityComparer<TK>.Default;
            var alookup = a.ToLookup(selectKeyA, cmp);
            var blookup = b.ToLookup(selectKeyB, cmp);

            var keys = new HashSet<TK>(alookup.Select(p => p.Key), cmp);
            keys.UnionWith(blookup.Select(p => p.Key));

            var join = from key in keys
                       let xa = alookup[key]
                       let xb = blookup[key]
                       select projection(xa, xb, key);

            return join.ToList();
        }

        public static IList<TR> FullOuterJoin<TA, TB, TK, TR>(
            this IEnumerable<TA> a,
            IEnumerable<TB> b,
            Func<TA, TK> selectKeyA,
            Func<TB, TK> selectKeyB,
            Func<TA, TB, TK, TR> projection,
            TA defaultA = default(TA),
            TB defaultB = default(TB),
            IEqualityComparer<TK> cmp = null) {
            cmp = cmp ?? EqualityComparer<TK>.Default;
            var alookup = a.ToLookup(selectKeyA, cmp);
            var blookup = (b ?? new List<TB>()).ToLookup(selectKeyB, cmp);

            var keys = new HashSet<TK>(alookup.Select(p => p.Key), cmp);
            keys.UnionWith(blookup.Select(p => p.Key));

            var join = from key in keys
                       from xa in alookup[key].DefaultIfEmpty(defaultA)
                       from xb in blookup[key].DefaultIfEmpty(defaultB)
                       select projection(xa, xb, key);

            return join.ToList();
        }
    
        /// <summary>
        /// Converts a byte array to Hexadecimal.
        /// </summary>
        /// <param name="bytes">The bytes to convert.</param>
        /// <returns>Hexadecimal string of the byte array.</returns>
        public static string ToHex(this IEnumerable<byte> bytes) {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}