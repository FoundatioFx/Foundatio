using System;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public static class LongRunningTaskQueueExtensions
    {
        public static string Enqueue<T>(this IQueue<LongRunningTaskData> queue, T jobData) {
            string id = Guid.NewGuid().ToString("N");
            var json = queue.Serializer.SerializeToString(jobData);
            queue.Enqueue(new LongRunningTaskData { Data = json, JobId = id, Type = typeof(T).AssemblyQualifiedName });

            return id;
        }
    }
}