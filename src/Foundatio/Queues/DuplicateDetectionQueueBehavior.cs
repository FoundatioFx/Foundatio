using System;
using System.Threading.Tasks;
using Foundatio.Caching;

namespace Foundatio.Queues {
    public class DuplicateDetectionQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly ICacheClient _cacheClient;
        private readonly TimeSpan _detectionWindow;

        public DuplicateDetectionQueueBehavior(ICacheClient cacheClient, TimeSpan? detectionWindow = null) {
            _cacheClient = cacheClient;
            _detectionWindow = detectionWindow ?? TimeSpan.FromMinutes(10);
        }
        
        protected override async Task OnEnqueuing(object sender, EnqueuingEventArgs<T> enqueuingEventArgs) {
            string uniqueIdentifier = GetUniqueIdentifier(enqueuingEventArgs.Data);
            if (String.IsNullOrEmpty(uniqueIdentifier))
                return;
            
            bool success = await _cacheClient.AddAsync(uniqueIdentifier, true, _detectionWindow);
            if (!success)
                enqueuingEventArgs.Cancel = true;
        }

        protected override async Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            string uniqueIdentifier = GetUniqueIdentifier(dequeuedEventArgs.Entry.Value);
            if (String.IsNullOrEmpty(uniqueIdentifier))
                return;
            
            await _cacheClient.RemoveAsync(uniqueIdentifier);
        }

        private string GetUniqueIdentifier(T data) {
            var haveUniqueIdentifier = data as IHaveUniqueIdentifier;
            return haveUniqueIdentifier?.UniqueIdentifier;
        }
    }

    public interface IHaveUniqueIdentifier {
        string UniqueIdentifier { get; }
    }
}