using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public class DuplicateDetectionQueueBehavior<T> : QueueBehaviorBase<T> where T : class {
        private readonly ICacheClient _cacheClient;
        private readonly ILoggerFactory _loggerFactory;
        private readonly TimeSpan _detectionWindow;

        public DuplicateDetectionQueueBehavior(ICacheClient cacheClient, ILoggerFactory loggerFactory, TimeSpan? detectionWindow = null) {
            _cacheClient = cacheClient;
            _loggerFactory = loggerFactory;
            _detectionWindow = detectionWindow ?? TimeSpan.FromMinutes(10);
        }
        
        protected override async Task OnEnqueuing(object sender, EnqueuingEventArgs<T> enqueuingEventArgs) {
            string uniqueIdentifier = GetUniqueIdentifier(enqueuingEventArgs.Data);
            if (String.IsNullOrEmpty(uniqueIdentifier))
                return;
            
            bool success = await _cacheClient.AddAsync(uniqueIdentifier, true, _detectionWindow);
            if (!success) {
                var logger = _loggerFactory.CreateLogger<T>();
                logger.LogInformation("Discarding queue entry due to duplicate {UniqueIdentifier}", uniqueIdentifier);
                enqueuingEventArgs.Cancel = true;
            }
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