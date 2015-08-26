using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Queues {
    public class ServiceBusQueue<T> : QueueBase<T> where T : class {
        private readonly string _queueName;
        private readonly NamespaceManager _namespaceManager;
        private readonly QueueClient _queueClient;
        private Action<QueueEntry<T>> _workerAction;
        private bool _workerAutoComplete;
        private bool _isWorking;
        private static object _workerLock = new object();
        private QueueDescription _queueDescription;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private long _workItmeTimeoutCount;
        private readonly int _retries;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(5);

        public ServiceBusQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, bool shouldRecreate = false, RetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null) : base(serializer, behaviors) {
            _queueName = queueName ?? typeof(T).Name;
            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            _retries = retries;
            if (workItemTimeout.HasValue && workItemTimeout.Value < TimeSpan.FromMinutes(5))
                _workItemTimeout = workItemTimeout.Value;

            if (_namespaceManager.QueueExists(_queueName) && shouldRecreate)
                _namespaceManager.DeleteQueue(_queueName);

            if (!_namespaceManager.QueueExists(_queueName)) {
                _queueDescription = new QueueDescription(_queueName) {
                    MaxDeliveryCount = retries + 1,
                    LockDuration = _workItemTimeout
                };
                _namespaceManager.CreateQueue(_queueDescription);
            } else {
                _queueDescription = _namespaceManager.GetQueue(_queueName);
                _queueDescription.MaxDeliveryCount = retries + 1;
                _queueDescription.LockDuration = _workItemTimeout;
            }

            _queueClient = QueueClient.CreateFromConnectionString(connectionString, _queueDescription.Path);
            if (retryPolicy != null)
                _queueClient.RetryPolicy = retryPolicy;
        }

        // TODO: Implement IQueueManager
        //public override void DeleteQueue() {
        //    if (_namespaceManager.QueueExists(_queueName))
        //        _namespaceManager.DeleteQueue(_queueName);

        //    _queueDescription = new QueueDescription(_queueName) {
        //        MaxDeliveryCount = _retries + 1,
        //        LockDuration = _workItemTimeout
        //    };
        //    _namespaceManager.CreateQueue(_queueDescription);

        //    _enqueuedCount = 0;
        //    _dequeuedCount = 0;
        //    _completedCount = 0;
        //    _abandonedCount = 0;
        //    _workerErrorCount = 0;
        //}

        public override async Task<QueueStats> GetQueueStatsAsync() {
            var q = await _namespaceManager.GetQueueAsync(_queueName);
            return new QueueStats {
                Queued = q.MessageCount,
                Working = -1,
                Deadletter = q.MessageCountDetails.DeadLetterMessageCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = _workItmeTimeoutCount
            };
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            throw new NotImplementedException();
        }

        private async Task OnMessage(BrokeredMessage message) {
            if (_workerAction == null)
                return;

            Interlocked.Increment(ref _dequeuedCount);
            var data = message.GetBody<T>();

            var workItem = new QueueEntry<T>(message.LockToken.ToString(), data, this, message.EnqueuedTimeUtc, message.DeliveryCount);
            try {
                _workerAction(workItem);
                if (_workerAutoComplete)
                    await workItem.CompleteAsync();
            } catch (Exception ex) {
                Interlocked.Increment(ref _workerErrorCount);
                Logger.Error().Exception(ex).Message("Error sending work item to worker: {0}", ex.Message).Write();
                await workItem.AbandonAsync();
            }
        }

        public override async Task<string> EnqueueAsync(T data) {
            if (!OnEnqueuing(data))
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new BrokeredMessage(data);
            await _queueClient.SendAsync(message);

            OnEnqueued(data, message.MessageId);

            return message.MessageId;
        }
        
        public override Task StartWorkingAsync(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (_isWorking)
                throw new ApplicationException("Already working.");

            lock (_workerLock) {
                _isWorking = true;
                _workerAction = handler;
                _workerAutoComplete = autoComplete;
                _queueClient.OnMessageAsync(OnMessage);
            }

            return Task.FromResult(0);
        }

        public override Task StopWorkingAsync() {
            if (_isWorking) {
                lock (_workerLock) {
                    _isWorking = false;
                    _workerAction = null;
                }
            }

            return Task.FromResult(0);
        }

        public override async Task<QueueEntry<T>> DequeueAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(30);

            using (var msg = await _queueClient.ReceiveAsync(timeout.Value)) {
                if (msg == null)
                    return null;
                
                var data = msg.GetBody<T>();
                Interlocked.Increment(ref _dequeuedCount);
                var entry = new QueueEntry<T>(msg.LockToken.ToString(), data, this, msg.EnqueuedTimeUtc, msg.DeliveryCount);
                OnDequeued(entry);
                return entry;
            }
        }

        public override async Task CompleteAsync(IQueueEntryMetadata entry) {
            Interlocked.Increment(ref _completedCount);
            await _queueClient.CompleteAsync(new Guid(entry.Id)).ConfigureAwait(false);
            OnCompleted(entry);
        }
        
        public override async Task AbandonAsync(IQueueEntryMetadata entry) {
            Interlocked.Increment(ref _abandonedCount);
            await _queueClient.AbandonAsync(new Guid(entry.Id)).ConfigureAwait(false);
            OnAbandoned(entry);
        }
        
        public override void Dispose() {
            base.Dispose();
            StopWorkingAsync().Wait();
            _queueClient.Close();
        }
    }
}
