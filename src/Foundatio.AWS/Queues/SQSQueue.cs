using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.AWS.Queues {
    public class SQSQueue<T> : QueueBase<T> where T : class {

        private readonly AmazonSQSClient _client;
        private readonly TimeSpan _workItemTimeout;
        private readonly TimeSpan _readQueueTimeout;
        private readonly RegionEndpoint _regionEndpoint;

        private string _queueUrl;

        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;



        public SQSQueue(
            string queueName,
            RegionEndpoint regionEndpoint = null,
            TimeSpan? workItemTimeout = null,
            TimeSpan? readQueueTimeout = null,
            ISerializer serializer = null,
            IEnumerable<IQueueBehavior<T>> behaviors = null,
            ILoggerFactory loggerFactory = null) : base(serializer, behaviors, loggerFactory) {

            _queueName = queueName;
            _workItemTimeout = workItemTimeout ?? TimeSpan.FromMinutes(5);
            _readQueueTimeout = readQueueTimeout ?? TimeSpan.FromSeconds(2);
            _regionEndpoint = regionEndpoint ?? RegionEndpoint.USEast1;

            _client = new AmazonSQSClient(_regionEndpoint);
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            try {
                var queueRequest = new GetQueueUrlRequest { QueueName = _queueName };
                var queueResponse = await _client.GetQueueUrlAsync(queueRequest, cancellationToken).ConfigureAwait(false);

                _queueUrl = queueResponse.QueueUrl;
            }
            catch (Exception ex) {
                throw new Exception($"Error accessing queue {_queueName} in region {_client.Config.RegionEndpoint}: {ex.Message}", ex);
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).ConfigureAwait(false))
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new SendMessageRequest {
                QueueUrl = _queueUrl,
                MessageBody = await _serializer.SerializeToStringAsync(data).ConfigureAwait(false),
            };

            var response = await _client.SendMessageAsync(message).ConfigureAwait(false);

            var entry = new QueueEntry<T>(response.MessageId, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).ConfigureAwait(false);

            return response.MessageId;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken cancellationToken) {
            var request = new ReceiveMessageRequest {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 1,
                VisibilityTimeout = (int)_workItemTimeout.TotalSeconds,
                WaitTimeSeconds = (int)_readQueueTimeout.TotalSeconds,
            };

            var response = await _client.ReceiveMessageAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.Messages.Count == 0)
                return null;

            Interlocked.Increment(ref _dequeuedCount);

            var message = response.Messages.First();
            var body = message.Body;
            var data = await _serializer.DeserializeAsync<T>(body).ConfigureAwait(false);
            var entry = new SQSQueueEntry<T>(message, data, this);

            await OnDequeuedAsync(entry).ConfigureAwait(false);

            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> queueEntry) {
            var entry = ToQueueEntry(queueEntry);
            var visibilityTimeout = Convert.ToInt32(_workItemTimeout.TotalSeconds);
            var request = new ChangeMessageVisibilityRequest {
                QueueUrl = _queueUrl,
                VisibilityTimeout = visibilityTimeout,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle
            };

            await _client.ChangeMessageVisibilityAsync(request).ConfigureAwait(false);
        }

        public override async Task CompleteAsync(IQueueEntry<T> queueEntry) {
            var entry = ToQueueEntry(queueEntry);
            var request = new DeleteMessageRequest {
                QueueUrl = _queueUrl,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle,
            };

            await _client.DeleteMessageAsync(request).ConfigureAwait(false);
        }

        public override async Task AbandonAsync(IQueueEntry<T> queueEntry) {
            var entry = ToQueueEntry(queueEntry);

            // re-queue and wait for processing
            var request = new ChangeMessageVisibilityRequest {
                QueueUrl = _queueUrl,
                VisibilityTimeout = (int)_workItemTimeout.TotalSeconds,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle,
            };

            await _client.ChangeMessageVisibilityAsync(request).ConfigureAwait(false);
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException();
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var attributeNames = new List<string> { QueueAttributeName.ApproximateNumberOfMessages };

            var queueAttributes = await _client.GetQueueAttributesAsync(_queueUrl, attributeNames).ConfigureAwait(false);

            return new QueueStats {
                Queued = queueAttributes.ApproximateNumberOfMessages,
                Working = 0,
                Deadletter = 0,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        public override Task DeleteQueueAsync() {
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
            return Task.CompletedTask;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            Task.Run(async () => {
                while (!cancellationToken.IsCancellationRequested) {
                    IQueueEntry<T> entry = null;
                    try {
                        entry = await DequeueImplAsync(cancellationToken).ConfigureAwait(false);
                        if (entry == null)
                            continue;

                        await handler(entry, cancellationToken).ConfigureAwait(false);
                        if (autoComplete && !entry.IsAbandoned && !entry.IsCompleted)
                            await entry.CompleteAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) {

                    }
                    catch (Exception ex) {
                        Interlocked.Increment(ref _workerErrorCount);

                        _logger.Error(ex, "Worker error: {0}", ex.Message);

                        if (entry != null && !entry.IsAbandoned && !entry.IsCompleted)
                            await entry.AbandonAsync().ConfigureAwait(false);
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _queueName, cancellationToken.IsCancellationRequested);
            });
        }

        public override void Dispose() {
            _logger.Trace("Queue {0} dispose", _queueName);

            base.Dispose();
        }

        private static SQSQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry) {
            var result = entry as SQSQueueEntry<T>;
            if (result == null)
                throw new ArgumentException($"Expected {nameof(SQSQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

            return result;
        }

    }
}
