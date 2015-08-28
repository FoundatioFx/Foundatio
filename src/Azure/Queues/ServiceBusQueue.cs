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
        private Func<QueueEntry<T>, Task> _workerAction;
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

        public override void DeleteQueue() {
            if (_namespaceManager.QueueExists(_queueName))
                _namespaceManager.DeleteQueue(_queueName);

            _queueDescription = new QueueDescription(_queueName) {
                MaxDeliveryCount = _retries + 1,
                LockDuration = _workItemTimeout
            };
            _namespaceManager.CreateQueue(_queueDescription);

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        public override QueueStats GetQueueStats()
        {
            var q = _namespaceManager.GetQueue(_queueName);
            return new QueueStats
            {
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

        public override IEnumerable<T> GetDeadletterItems() {
            throw new NotImplementedException();
        }

        private async Task OnMessage(BrokeredMessage message) {
            if (_workerAction == null)
                return;

            Interlocked.Increment(ref _dequeuedCount);
            var data = message.GetBody<T>();

            var workItem = new QueueEntry<T>(message.LockToken.ToString(), data, this, message.EnqueuedTimeUtc, message.DeliveryCount);
            try {
                await _workerAction(workItem);
                if (_workerAutoComplete)
                    workItem.Complete();
            } catch (Exception ex) {
                Interlocked.Increment(ref _workerErrorCount);
                Logger.Error().Exception(ex).Message("Error sending work item to worker: {0}", ex.Message).Write();
                workItem.Abandon();
            }
        }

        public Task EnqueueAsync(T data) {
            if (!OnEnqueuing(data))
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            return _queueClient.SendAsync(new BrokeredMessage(data));
        }

        public override string Enqueue(T data) {
            if (!OnEnqueuing(data))
                return null;
            Interlocked.Increment(ref _enqueuedCount);
            var msg = new BrokeredMessage(data);
            _queueClient.Send(msg);

            OnEnqueued(data, msg.MessageId);

            return msg.MessageId;
        }

        public override void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken token = default(CancellationToken)) {
            StartWorking(entry => {
                handler(entry);
                return TaskHelper.Completed();
            }, autoComplete);
        }

        public void StartWorking(Func<QueueEntry<T>, Task> handler, bool autoComplete = false) {
            if (_isWorking)
                throw new ApplicationException("Already working.");

            lock (_workerLock) {
                _isWorking = true;
                _workerAction = handler;
                _workerAutoComplete = autoComplete;
                _queueClient.OnMessageAsync(OnMessage);
            }
        }

        public  void StopWorking() {
            if (!_isWorking)
                return;

            lock (_workerLock) {
                _isWorking = false;
                _workerAction = null;
            }
        }

        public override QueueEntry<T> Dequeue(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(30);

            using (var msg = _queueClient.Receive(timeout.Value)) {
                if (msg == null)
                    return null;
                
                var data = msg.GetBody<T>();
                Interlocked.Increment(ref _dequeuedCount);
                var entry = new QueueEntry<T>(msg.LockToken.ToString(), data, this, msg.EnqueuedTimeUtc, msg.DeliveryCount);
                OnDequeued(entry);
                return entry;
            }
        }

        public async Task CompleteAsync(IQueueEntryMetadata entry) {
            Interlocked.Increment(ref _completedCount);
            await _queueClient.CompleteAsync(new Guid(entry.Id)).ConfigureAwait(false);
            OnCompleted(entry);
        }

        public override void Complete(IQueueEntryMetadata entry)
        {
            CompleteAsync(entry).Wait();
        }

        public async Task AbandonAsync(IQueueEntryMetadata entry) {
            Interlocked.Increment(ref _abandonedCount);
            await _queueClient.AbandonAsync(new Guid(entry.Id)).ConfigureAwait(false);
            OnAbandoned(entry);
        }

        public override void Abandon(IQueueEntryMetadata entry)
        {
            AbandonAsync(entry).Wait();
        }

        public override void Dispose() {
            base.Dispose();
            StopWorking();
            _queueClient.Close();
        }
    }
}
