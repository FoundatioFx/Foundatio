using System;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Jobs {
    public class WorkItemContext : JobQueueEntryContext<WorkItemData> {
        private readonly Func<int, string, Task> _progressCallback;

        public WorkItemContext(JobQueueEntryContext<WorkItemData> context, object data, string jobId, ILock workItemLock, Func<int, string, Task> progressCallback) : base(context.QueueEntry, context.QueueEntryLock, context.CancellationToken) {
            Data = data;
            JobId = jobId;
            WorkItemLock = workItemLock;
            _progressCallback = progressCallback;
        }

        public object Data { get; private set; }
        public string JobId { get; private set; }
        public ILock WorkItemLock { get; private set; }

        public Task ReportProgressAsync(int progress, string message = null) {
            return _progressCallback(progress, message);
        }

        public T GetData<T>() where T : class {
            return Data as T;
        }
    }
}