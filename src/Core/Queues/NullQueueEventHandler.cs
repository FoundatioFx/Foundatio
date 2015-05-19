namespace Foundatio.Queues {
    public class NullQueueEventHandler<T> : IQueueEventHandler<T> where T : class {
        public bool BeforeEnqueue(IQueue<T> queue, T data) {
            return true;
        }

        public void AfterEnqueue(IQueue<T> queue, string id, T data) { }
        public void OnDequeue(IQueue<T> queue, string id, T data) { }
        public void OnComplete(IQueue<T> queue, string id) { }
        public void OnAbandon(IQueue<T> queue, string id) { }
        
        public static IQueueEventHandler<T> Instance = new NullQueueEventHandler<T>(); 
    }
}