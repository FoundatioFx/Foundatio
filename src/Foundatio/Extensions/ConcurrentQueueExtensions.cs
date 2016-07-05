using System;
using System.Collections.Concurrent;

namespace Foundatio.Extensions {
    internal static class ConcurrentQueueExtensions {
        public static void Clear<T>(this ConcurrentQueue<T> queue) {
            T item;
            while (queue.TryDequeue(out item)) { }
        }
    }
}
