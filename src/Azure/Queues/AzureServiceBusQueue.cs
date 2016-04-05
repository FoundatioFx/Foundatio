using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Queues {
    public class AzureServiceBusQueue<T> : QueueBase<T> where T : class {
        private readonly string _queueName;
        private readonly NamespaceManager _namespaceManager;
        private readonly Func<Task<QueueDescription>> _createQueue;
        private readonly Func<string, QueueClient> _clientFactory;
        private Action _disposeClient = () => { };
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;

        [Obsolete("This constructor runs synchronously. Prefer the async Factory.Build method.")]
        public AzureServiceBusQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, bool shouldRecreate = false, RetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null) : base(serializer, behaviors, loggerFactory) {
            _queueName = queueName ?? typeof(T).Name;
            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);

            workItemTimeout = workItemTimeout ?? TimeSpan.FromMinutes(5);
            if (workItemTimeout.Value > TimeSpan.FromMinutes(5))
                workItemTimeout = TimeSpan.FromMinutes(5);

            _createQueue = MemoizeQueueFactory(_namespaceManager, queueName, workItemTimeout.Value, retries);

            if (_namespaceManager.QueueExists(_queueName) && shouldRecreate)
                _namespaceManager.DeleteQueue(_queueName);

            // maintain previous functionality: go ahead and synchronously create queue and client
            var queueDescription = EnsureQueueExistsAsync().Result;

            _clientFactory = MemoizeClientFactory(connectionString, retryPolicy);
            var ignoreResult = _clientFactory(queueDescription.Path);
        }

        private AzureServiceBusQueue(string queueName, NamespaceManager namespaceManager, Func<Task<QueueDescription>> queueFactory, Func<string, QueueClient> clientFactory, ISerializer serializer, IEnumerable<IQueueBehavior<T>> behaviors, ILoggerFactory loggerFactory) : base(serializer, behaviors, loggerFactory) {
            _queueName = queueName;
            _namespaceManager = namespaceManager;
            _createQueue = queueFactory;
            _clientFactory = clientFactory;
        }

        public class Factory {
            private readonly string _connectionString;
            private string _queueName;
            private int _retries;
            private TimeSpan _workItemTimeout;
            private bool _shouldRecreate;
            private bool _lazyConnect;
            private RetryPolicy _retryPolicy;
            private ISerializer _serializer;
            private IEnumerable<IQueueBehavior<T>> _behaviors;
            private ILoggerFactory _loggerFactory;

            public Factory(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, bool shouldRecreate = false, bool lazyConnect = true, RetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null) {
                _connectionString = connectionString;
                Queue(queueName);
                _retries = retries;
                Timeout(workItemTimeout ?? TimeSpan.FromMinutes(5));
                _shouldRecreate = shouldRecreate;
                _lazyConnect = lazyConnect;
                _retryPolicy = retryPolicy;
                _serializer = serializer;
                _behaviors = behaviors;
                _loggerFactory = loggerFactory;
            }

            public Factory Queue(string queueName) {
                _queueName = queueName ?? typeof(T).Name;
                return this;
            }

            public Factory Retries(int retries) {
                _retries = retries;
                return this;
            }

            public Factory Timeout(TimeSpan workItemTimeout) {
                if (workItemTimeout <= TimeSpan.FromMinutes(5)) {
                    _workItemTimeout = workItemTimeout;
                }
                return this;
            }

            public Factory RecreateQueue(bool shouldRecreate) {
                _shouldRecreate = shouldRecreate;
                return this;
            }

            public Factory LazyConnect(bool lazyConnect) {
                _lazyConnect = lazyConnect;
                return this;
            }

            public Factory RetryPolicy(RetryPolicy retryPolicy) {
                _retryPolicy = retryPolicy;
                return this;
            }

            public Factory Serializer(ISerializer serializer) {
                _serializer = serializer;
                return this;
            }

            public Factory Behaviors(IEnumerable<IQueueBehavior<T>> behaviors) {
                _behaviors = behaviors;
                return this;
            }

            public Factory LoggerFactory(ILoggerFactory loggerFactory) {
                _loggerFactory = loggerFactory;
                return this;
            }

            public async Task<AzureServiceBusQueue<T>> Build() {
                var namespaceManager = NamespaceManager.CreateFromConnectionString(_connectionString);

                var createQueue = MemoizeQueueFactory(namespaceManager, _queueName, _workItemTimeout, _retries);
                var createClient = MemoizeClientFactory(_connectionString, _retryPolicy);

                if (!_lazyConnect) {
                    if (_shouldRecreate && await namespaceManager.QueueExistsAsync(_queueName).AnyContext()) {
                        await namespaceManager.DeleteQueueAsync(_queueName).AnyContext();
                    }

                    var description = await createQueue().AnyContext();
                    var ignoreResult = createClient(description.Path);
                }

                return new AzureServiceBusQueue<T>(_queueName, namespaceManager, createQueue, createClient, _serializer, _behaviors, _loggerFactory);
            }
        }

        public override async Task DeleteQueueAsync() {
            if (await _namespaceManager.QueueExistsAsync(_queueName).AnyContext()) {
                await _namespaceManager.DeleteQueueAsync(_queueName).AnyContext();
            }

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            await EnsureQueueExistsAsync().AnyContext();

            var q = await _namespaceManager.GetQueueAsync(_queueName).AnyContext();
            return new QueueStats {
                Queued = q.MessageCount,
                Working = 0,
                Deadletter = q.MessageCountDetails.DeadLetterMessageCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            throw new NotImplementedException();
        }
        
        public override async Task<string> EnqueueAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            var queueClient = await EnsureQueueExistsAsync().AnyContext();

            Interlocked.Increment(ref _enqueuedCount);
            var message = new BrokeredMessage(data);
            await queueClient.SendAsync(message).AnyContext();
            
            var entry = new QueueEntry<T>(message.MessageId, data, this, DateTime.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return message.MessageId;
        }
        
        public override void StartWorking(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // revisit: can StartWorking be made to return Task? it's a breaking change - perhaps mark obsolete and add a new async version?
            var queueClient = EnsureQueueExistsAsync().Result;

            queueClient.OnMessageAsync(async msg => {
                var queueEntry = await HandleDequeueAsync(msg);

                try {
                    await handler(queueEntry, cancellationToken).AnyContext();
                    if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.CompleteAsync().AnyContext();
                } catch (Exception ex) {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.Error(ex, "Error sending work item to worker: {0}", ex.Message);

                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.AbandonAsync().AnyContext();
                }
            });
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null) {
            var queueClient = await EnsureQueueExistsAsync().AnyContext();

            using (var msg = await queueClient.ReceiveAsync(timeout ?? TimeSpan.FromSeconds(30)).AnyContext()) {
                return await HandleDequeueAsync(msg).AnyContext();
            }
        }

        public override Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken) {
            _logger.Warn("Azure Service Bus does not support CancellationTokens - use TimeSpan overload instead. Using default 30 second timeout.");

            return DequeueAsync();
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            var queueClient = await EnsureQueueExistsAsync().AnyContext();

            await queueClient.RenewMessageLockAsync(new Guid(entry.Id)).AnyContext();
            await OnLockRenewedAsync(entry).AnyContext();
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            var queueClient = await EnsureQueueExistsAsync().AnyContext();

            Interlocked.Increment(ref _completedCount);
            await queueClient.CompleteAsync(new Guid(entry.Id)).AnyContext();
            await OnCompletedAsync(entry).AnyContext();
        }
        
        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            var queueClient = await EnsureQueueExistsAsync().AnyContext();

            Interlocked.Increment(ref _abandonedCount);
            await queueClient.AbandonAsync(new Guid(entry.Id)).AnyContext();
            await OnAbandonedAsync(entry).AnyContext();
        }
        
        public override void Dispose() {
            _disposeClient();
            base.Dispose();
        }

        private async Task<QueueClient> EnsureQueueExistsAsync() {
            var description = await _createQueue().AnyContext();
            var client = _clientFactory(description.Path);

            _disposeClient = () => client.Close();
            return client;
        }

        private async Task<IQueueEntry<T>> HandleDequeueAsync(BrokeredMessage msg) {
            if (msg == null)
                return null;

            var data = msg.GetBody<T>();
            Interlocked.Increment(ref _dequeuedCount);
            var entry = new QueueEntry<T>(msg.LockToken.ToString(), data, this, msg.EnqueuedTimeUtc, msg.DeliveryCount);
            await OnDequeuedAsync(entry).AnyContext();
            return entry;
        }

        private static Func<Task<QueueDescription>> MemoizeQueueFactory(NamespaceManager namespaceManager, string queueName, TimeSpan workItemTimeout, int retries) {
            return async () => {
                var queueDescription = default(QueueDescription);

                if (!await namespaceManager.QueueExistsAsync(queueName).AnyContext()) {
                    queueDescription = new QueueDescription(queueName) {
                        MaxDeliveryCount = retries + 1,
                        LockDuration = workItemTimeout
                    };
                    await namespaceManager.CreateQueueAsync(queueDescription).AnyContext();
                } else {
                    queueDescription = await namespaceManager.GetQueueAsync(queueName).AnyContext();

                    var changes = false;
                    var newMaxDeliveryCount = retries + 1;
                    if (queueDescription.MaxDeliveryCount != newMaxDeliveryCount) {
                        queueDescription.MaxDeliveryCount = newMaxDeliveryCount;
                        changes = true;
                    }

                    if (queueDescription.LockDuration != workItemTimeout) {
                        queueDescription.LockDuration = workItemTimeout;
                        changes = true;
                    }

                    if (changes) {
                        await namespaceManager.UpdateQueueAsync(queueDescription).AnyContext();
                    }
                }

                return queueDescription;
            };
        }

        private static Func<string, QueueClient> MemoizeClientFactory(string connectionString, RetryPolicy retryPolicy) {
            var client = default(QueueClient);
            return path => {
                if (client == null) {
                    client = QueueClient.CreateFromConnectionString(connectionString, path);

                    if (retryPolicy != null)
                        client.RetryPolicy = retryPolicy;
                }

                return client;
            };
        }
    }
}
