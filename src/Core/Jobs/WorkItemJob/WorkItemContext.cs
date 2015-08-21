using System;

namespace Foundatio.Jobs {
    public class WorkItemContext {
        private readonly Action<int, string> _progressCallback;

        public WorkItemContext(object data, string jobId, Action<int, string> progressCallback) {
            Data = data;
            JobId = jobId;
            _progressCallback = progressCallback;
        }

        public object Data { get; private set; }
        public string JobId { get; private set; }

        public void ReportProgress(int progress, string message = null) {
            _progressCallback(progress, message);
        }

        public T GetData<T>() where T : class {
            return Data as T;
        }
    }
}