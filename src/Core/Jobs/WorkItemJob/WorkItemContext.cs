using System;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Jobs {
    public class WorkItemContext {
        private readonly Func<int, string, Task> _progressCallback;

        public WorkItemContext(object data, string jobId, Func<int, string, Task> progressCallback) {
            Data = data;
            JobId = jobId;
            _progressCallback = progressCallback;
        }

        public object Data { get; private set; }
        public string JobId { get; private set; }

        public async Task ReportProgressAsync(int progress, string message = null) {
            await _progressCallback(progress, message).AnyContext();
        }

        public T GetData<T>() where T : class {
            return Data as T;
        }
    }
}