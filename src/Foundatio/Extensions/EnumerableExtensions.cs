﻿using System;
using System.Collections.Generic;

namespace Foundatio.Utility {
    internal static class EnumerableExtensions {
        public static void ForEach<T>(this IEnumerable<T> collection, Action<T> action) {
            if (collection == null || action == null)
                return;

            foreach (var item in collection)
                action(item);
        }

        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> range) {
            if (list == null || range == null)
                return;

            foreach (var r in range)
                list.Add(r);
        }
    }
}