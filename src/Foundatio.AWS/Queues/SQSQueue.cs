using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Nito.AsyncEx;
using ThirdParty.Json.LitJson;

namespace Foundatio.Queues {


    public class SQSQueue<T> : QueueBase<T> where T : class {
        private readonly AsyncLock _lock = new AsyncLock();

        private readonly Lazy<AmazonSQSClient> _client;
        private readonly AWSCredentials _credentials;
        private readonly RegionEndpoint _regionEndpoint;
        private readonly SQSQueueOptions _options;

        private readonly int _workItemTimeoutSeconds;
        private readonly int _readQueueTimeoutSeconds;

        private string _queueUrl;
        private string _deadUrl;


        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;

        public SQSQueue(
            string queueName,
            AWSCredentials credentials = null,
            RegionEndpoint regionEndpoint = null,
            SQSQueueOptions options = null,
            ISerializer serializer = null,
            IEnumerable<IQueueBehavior<T>> behaviors = null,
            ILoggerFactory loggerFactory = null)
            : base(serializer, behaviors, loggerFactory) {

            _queueName = queueName ?? throw new ArgumentNullException(nameof(queueName));

            _credentials = credentials ?? FallbackCredentialsFactory.GetCredentials();
            _regionEndpoint = regionEndpoint ?? RegionEndpoint.USEast1;
            _options = options ?? new SQSQueueOptions();

            _workItemTimeoutSeconds = Convert.ToInt32(_options.WorkItemTimeout.TotalSeconds);
            _readQueueTimeoutSeconds = Convert.ToInt32(_options.ReadQueueTimeout.TotalSeconds);

            _client = new Lazy<AmazonSQSClient>(() => new AmazonSQSClient(_credentials, _regionEndpoint));
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            if (!String.IsNullOrEmpty(_queueUrl))
                return;

            using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false)) {
                if (!String.IsNullOrEmpty(_queueUrl))
                    return;

                try {
                    var urlResponse = await _client.Value.GetQueueUrlAsync(_queueName, cancellationToken).AnyContext();
                    _queueUrl = urlResponse.QueueUrl;
                }
                catch (QueueDoesNotExistException ex) {
                    if (!_options.CanCreateQueue)
                        throw;
                }


                if (!String.IsNullOrEmpty(_queueUrl))
                    return;

                await CreateQueueAsync();
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            var message = new SendMessageRequest {
                QueueUrl = _queueUrl,
                MessageBody = await _serializer.SerializeToStringAsync(data).AnyContext(),
            };

            var response = await _client.Value.SendMessageAsync(message).AnyContext();

            Interlocked.Increment(ref _enqueuedCount);
            var entry = new QueueEntry<T>(response.MessageId, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return response.MessageId;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            var visibilityTimeout = _workItemTimeoutSeconds;

            // sqs doesn't support already canceled token, change timeout and token for sqs pattern
            var waitTimeout = linkedCancellationToken.IsCancellationRequested ? 0 : _readQueueTimeoutSeconds;
            var cancel = linkedCancellationToken.IsCancellationRequested ? CancellationToken.None : linkedCancellationToken;

            var request = new ReceiveMessageRequest {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 1,
                VisibilityTimeout = visibilityTimeout,
                WaitTimeSeconds = waitTimeout,
                AttributeNames = new List<string> { "ApproximateReceiveCount", "SentTimestamp" }
            };

            // receive message local function
            async Task<ReceiveMessageResponse> receiveMessageAsync()
            {
                try {
                    return await _client.Value.ReceiveMessageAsync(request, cancel).AnyContext();
                }
                catch (OperationCanceledException) {
                    return null;
                }
            }

            var response = await receiveMessageAsync().AnyContext();
            // retry loop
            while (response == null && !linkedCancellationToken.IsCancellationRequested) {
                try {
                    await SystemClock.SleepAsync(_options.DequeueInterval, linkedCancellationToken).AnyContext();
                }
                catch (OperationCanceledException) { }

                response = await receiveMessageAsync().AnyContext();
            }

            if (response == null || response.Messages.Count == 0)
                return null;

            Interlocked.Increment(ref _dequeuedCount);

            var message = response.Messages.First();
            var body = message.Body;
            var data = await _serializer.DeserializeAsync<T>(body).AnyContext();
            var entry = new SQSQueueEntry<T>(message, data, this);

            await OnDequeuedAsync(entry).AnyContext();

            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> queueEntry) {
            var entry = ToQueueEntry(queueEntry);

            var request = new ChangeMessageVisibilityRequest {
                QueueUrl = _queueUrl,
                VisibilityTimeout = _workItemTimeoutSeconds,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle
            };

            await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();
        }

        public override async Task CompleteAsync(IQueueEntry<T> queueEntry) {
            if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var entry = ToQueueEntry(queueEntry);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var request = new DeleteMessageRequest {
                QueueUrl = _queueUrl,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle,
            };

            await _client.Value.DeleteMessageAsync(request).AnyContext();

            Interlocked.Increment(ref _completedCount);
            queueEntry.MarkCompleted();

            await OnCompletedAsync(queueEntry).AnyContext();

        }

        public override async Task AbandonAsync(IQueueEntry<T> queueEntry) {
            if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var entry = ToQueueEntry(queueEntry);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            // re-queue and wait for processing
            var request = new ChangeMessageVisibilityRequest {
                QueueUrl = _queueUrl,
                VisibilityTimeout = _workItemTimeoutSeconds,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle,
            };

            await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();

            Interlocked.Increment(ref _abandonedCount);
            queueEntry.MarkAbandoned();

            await OnAbandonedAsync(queueEntry).AnyContext();
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException();
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var attributeNames = new List<string> { QueueAttributeName.ApproximateNumberOfMessages, QueueAttributeName.RedrivePolicy };
            var queueRequest = new GetQueueAttributesRequest(_queueUrl, attributeNames);
            var queueAttributes = await _client.Value.GetQueueAttributesAsync(queueRequest).AnyContext();

            int queueCount = queueAttributes.ApproximateNumberOfMessages;
            int deadCount = 0;

            // dead letter supported
            if (!_options.SupportDeadLetter) {
                return new QueueStats {
                    Queued = queueCount,
                    Working = 0,
                    Deadletter = deadCount,
                    Enqueued = _enqueuedCount,
                    Dequeued = _dequeuedCount,
                    Completed = _completedCount,
                    Abandoned = _abandonedCount,
                    Errors = _workerErrorCount,
                    Timeouts = 0
                };
            }

            // lookup dead letter url
            if (String.IsNullOrEmpty(_deadUrl)) {
                var deadLetterName = queueAttributes.Attributes.DeadLetterQueue();
                if (!String.IsNullOrEmpty(deadLetterName)) {
                    var deadResponse = await _client.Value.GetQueueUrlAsync(deadLetterName).AnyContext();
                    _deadUrl = deadResponse.QueueUrl;
                }
            }

            // get attributes from dead letter
            if (!String.IsNullOrEmpty(_deadUrl)) {
                var deadRequest = new GetQueueAttributesRequest(_deadUrl, attributeNames);
                var deadAttributes = await _client.Value.GetQueueAttributesAsync(deadRequest).AnyContext();
                deadCount = deadAttributes.ApproximateNumberOfMessages;
            }

            return new QueueStats {
                Queued = queueCount,
                Working = 0,
                Deadletter = deadCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        public override async Task DeleteQueueAsync() {
            if (!String.IsNullOrEmpty(_queueUrl)) {
                var response = await _client.Value.DeleteQueueAsync(_queueUrl).AnyContext();
            }
            if (!String.IsNullOrEmpty(_deadUrl)) {
                var response = await _client.Value.DeleteQueueAsync(_deadUrl).AnyContext();
            }

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);

            Task.Run(async () => {
                _logger.Trace("WorkerLoop Start {_queueName}", _queueName);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.Trace("WorkerLoop Signaled {_queueName}", _queueName);

                    IQueueEntry<T> entry = null;
                    try {
                        entry = await DequeueImplAsync(linkedCancellationToken).AnyContext();
                    }
                    catch (OperationCanceledException) { }

                    if (linkedCancellationToken.IsCancellationRequested || entry == null)
                        continue;

                    try {
                        await handler(entry, linkedCancellationToken).AnyContext();
                        if (autoComplete && !entry.IsAbandoned && !entry.IsCompleted)
                            await entry.CompleteAsync().AnyContext();
                    }
                    catch (Exception ex) {
                        Interlocked.Increment(ref _workerErrorCount);
                        _logger.Error(ex, "Worker error: {0}", ex.Message);

                        if (entry != null && !entry.IsAbandoned && !entry.IsCompleted)
                            await entry.AbandonAsync().AnyContext();
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _queueName, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken);
        }

        public override void Dispose() {
            base.Dispose();

            if (_client.IsValueCreated)
                _client.Value.Dispose();
        }

        protected virtual async Task CreateQueueAsync() {
            // step 1, create queue
            var createQueueRequest = new CreateQueueRequest { QueueName = _queueName };
            var createQueueResponse = await _client.Value.CreateQueueAsync(createQueueRequest).AnyContext();
            _queueUrl = createQueueResponse.QueueUrl;

            if (!_options.SupportDeadLetter)
                return;

            // step 2, create dead letter queue
            var createDeadRequest = new CreateQueueRequest { QueueName = _queueName + "-deadletter" };
            var createDeadResponse = await _client.Value.CreateQueueAsync(createDeadRequest).AnyContext();
            _deadUrl = createDeadResponse.QueueUrl;


            // step 3, get dead letter attributes
            var attributeNames = new List<string> { QueueAttributeName.QueueArn };
            var deadAttributeRequest = new GetQueueAttributesRequest(_deadUrl, attributeNames);
            var deadAttributeResponse = await _client.Value.GetQueueAttributesAsync(deadAttributeRequest).AnyContext();

            // step 4, set redrive policy
            var redrivePolicy = new JsonData();
            redrivePolicy["maxReceiveCount"] = _options.RetryCount.ToString();
            redrivePolicy["deadLetterTargetArn"] = deadAttributeResponse.QueueARN;

            var attributes = new Dictionary<string, string>();
            attributes[QueueAttributeName.RedrivePolicy] = JsonMapper.ToJson(redrivePolicy);

            var setAttributeRequest = new SetQueueAttributesRequest(_queueUrl, attributes);
            var setAttributeResponse = await _client.Value.SetQueueAttributesAsync(setAttributeRequest).AnyContext();
        }

        private static SQSQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry) {
            var result = entry as SQSQueueEntry<T>;
            if (result == null)
                throw new ArgumentException($"Expected {nameof(SQSQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

            return result;
        }
    }
}
