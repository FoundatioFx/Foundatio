using System.Collections.Concurrent;

namespace Foundatio.Utility {
    internal static class ConcurrentQueueExtensions {
        public static void Clear<T>(this ConcurrentQueue<T> queue) {
            while (queue.TryDequeue(out var _)) { }
        }
    }
}
