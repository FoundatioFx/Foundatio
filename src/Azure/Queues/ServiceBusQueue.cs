using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using NLog.Fluent;

namespace Foundatio.Azure.Queues {
    public class ServiceBusQueue<T> : IQueue<T> where T : class {
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
        private readonly int _retries;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(5);

        public ServiceBusQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, bool shouldRecreate = false, RetryPolicy retryPolicy = null) {
            _queueName = queueName ?? typeof(T).Name;
            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            _retries = retries;
            if (workItemTimeout.HasValue && workItemTimeout.Value < TimeSpan.FromMinutes(5))
                _workItemTimeout = workItemTimeout.Value;
            QueueId = Guid.NewGuid().ToString("N");

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

        public void DeleteQueue() {
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

        public long EnqueuedCount { get { return _enqueuedCount; } }
        public long DequeuedCount { get { return _dequeuedCount; } }
        public long CompletedCount { get { return _completedCount; } }
        public long AbandonedCount { get { return _abandonedCount; } }
        public long WorkerErrorCount { get { return _workerErrorCount; } }

        public string QueueId { get; private set; }

        public long GetQueueCount() {
            var q = _namespaceManager.GetQueue(_queueName);
            return q.MessageCount;
        }

        public long GetWorkingCount() { return -1; }

        public long GetDeadletterCount() {
            var q = _namespaceManager.GetQueue(_queueName);
            return q.MessageCountDetails.DeadLetterMessageCount;
        }

        public IEnumerable<T> GetDeadletterItems() {
            throw new NotImplementedException();
        }

        private async Task OnMessage(BrokeredMessage message) {
            if (_workerAction == null)
                return;

            Interlocked.Increment(ref _dequeuedCount);
            var data = message.GetBody<T>();

            var workItem = new QueueEntry<T>(message.LockToken.ToString(), data, this);
            try {
                await _workerAction(workItem);
                if (_workerAutoComplete)
                    workItem.Complete();
            } catch (Exception ex) {
                Interlocked.Increment(ref _workerErrorCount);
                Log.Error().Exception(ex).Message("Error sending work item to worker: {0}", ex.Message).Write();
                workItem.Abandon();
            }
        }

        public Task EnqueueAsync(T data) {
            Interlocked.Increment(ref _enqueuedCount);
            return _queueClient.SendAsync(new BrokeredMessage(data));
        }

        public string Enqueue(T data) {
            Interlocked.Increment(ref _enqueuedCount);
            var msg = new BrokeredMessage(data);
            _queueClient.Send(msg);
            return msg.MessageId;
        }

        public void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false) {
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

        public void StopWorking() {
            if (!_isWorking)
                return;

            lock (_workerLock) {
                _isWorking = false;
                _workerAction = null;
            }
        }

        public QueueEntry<T> Dequeue(TimeSpan? timeout = null) {
            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(30);

            using (var msg = _queueClient.Receive(timeout.Value)) {
                if (msg == null)
                    return null;

                Interlocked.Increment(ref _dequeuedCount);
                var data = msg.GetBody<T>();
                return new QueueEntry<T>(msg.LockToken.ToString(), data, this);
            }
        }

        public Task CompleteAsync(string id) {
            Interlocked.Increment(ref _completedCount);
            return _queueClient.CompleteAsync(new Guid(id));
        }

        public void Complete(string id) {
            Interlocked.Increment(ref _completedCount);
            _queueClient.Complete(new Guid(id));
        }

        public Task AbandonAsync(string id) {
            Interlocked.Increment(ref _abandonedCount);
            return _queueClient.AbandonAsync(new Guid(id));
        }

        public void Abandon(string id) {
            Interlocked.Increment(ref _abandonedCount);
            _queueClient.Abandon(new Guid(id));
        }

        public void Dispose() {
            StopWorking();
            _queueClient.Close();
        }
    }
}
