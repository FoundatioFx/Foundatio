using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;

namespace Foundatio.Jobs {
    public class WorkItemContext : JobQueueEntryContext<WorkItemData> {
        private readonly Func<int, string, Task> _progressCallback;

        public WorkItemContext(JobQueueEntryContext<WorkItemData> context, object data, string jobId, ILock workItemLock, Func<int, string, Task> progressCallback) : base(context.QueueEntry, context.JobLock, context.QueueEntryLock, context.CancellationToken) {
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

        public override async Task RenewLocksAsync() {
            if (WorkItemLock != null)
                await WorkItemLock.RenewAsync().AnyContext();

            await base.RenewLocksAsync().AnyContext();
        }

        public T GetData<T>() where T : class {
            return Data as T;
        }
    }
}