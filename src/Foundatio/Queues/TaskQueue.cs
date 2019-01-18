using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Queues {
    public class TaskQueue: IDisposable {
        private readonly ConcurrentQueue<Func<Task>> _queue = new ConcurrentQueue<Func<Task>>();
        private readonly SemaphoreSlim _semaphore;
        private readonly AsyncAutoResetEvent _autoResetEvent = new AsyncAutoResetEvent();

        private CancellationTokenSource _workLoopCancellationTokenSource;
        private readonly int _maxItems;
        private int _working;
        private readonly Action _queueEmptyAction;
        private readonly ILogger _logger;

        public TaskQueue(int maxItems = Int32.MaxValue, byte maxDegreeOfParallelism = 1, bool autoStart = true, Action queueEmptyAction = null, ILoggerFactory loggerFactory = null) {
            _maxItems = maxItems;
            _queueEmptyAction = queueEmptyAction;
            _semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
            _logger = loggerFactory?.CreateLogger<TaskQueue>() ?? NullLogger<TaskQueue>.Instance;

            if (autoStart)
                Start();
        }

        public int Queued => _queue.Count;
        public int Working => _working;

        public bool Enqueue(Func<Task> task) {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (_queue.Count >= _maxItems) {
                _logger.LogError("Background worker task queue is full, task has been discarded.");
                return false;
            }

            _queue.Enqueue(task);
            _autoResetEvent.Set();
            return true;
        }

        public void Start(CancellationToken token = default) {
            _workLoopCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            StartWorking();
        }

        private void StartWorking() {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled) _logger.LogTrace("Starting task background worker loop.");
            Task.Run(async () => {
                while (!_workLoopCancellationTokenSource.Token.IsCancellationRequested) {
                    try {
                        bool workerAvailable = await _semaphore.WaitAsync(1000, _workLoopCancellationTokenSource.Token).AnyContext();
                        if (!_queue.TryDequeue(out var task)) {
                            if (workerAvailable)
                                _semaphore.Release();

                            if (_queue.IsEmpty) {
                                if (isTraceLogLevelEnabled) _logger.LogTrace("Waiting to dequeue background worker task.");
                                try {
                                    using (var timeoutCancellationTokenSource = new CancellationTokenSource(10000))
                                    using (var dequeueCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_workLoopCancellationTokenSource.Token, timeoutCancellationTokenSource.Token)) {
                                        await _autoResetEvent.WaitAsync(dequeueCancellationTokenSource.Token).AnyContext();
                                    }
                                } catch (OperationCanceledException) { }
                            }

                            continue;
                        }

                        Interlocked.Increment(ref _working);
                        if (isTraceLogLevelEnabled) _logger.LogTrace("Running dequeued background worker task");
                        // TODO: Cancel after x amount of time.
                        var unawaited = Task.Run(() => task(), _workLoopCancellationTokenSource.Token)
                            .ContinueWith(t => {
                                Interlocked.Decrement(ref _working);
                                _semaphore.Release();

                                if (t.IsFaulted) {
                                    var ex = t.Exception.InnerException;
                                    _logger.LogError(ex, "Error running dequeue background worker task: {Message}", ex?.Message);
                                } else if (t.IsCanceled) {
                                    _logger.LogWarning("Dequeue background worker task was cancelled.");
                                } else if (isTraceLogLevelEnabled) {
                                    _logger.LogTrace("Finished running dequeued background worker task.");
                                }

                                if (_queueEmptyAction != null && _working == 0 && _queue.IsEmpty && _queue.Count == 0) {
                                    if (isTraceLogLevelEnabled) _logger.LogTrace("Running completed background worker action.");
                                    // NOTE: There could be a race here where an a semaphore was taken but the queue was empty.
                                    _queueEmptyAction();
                                }
                            });
                    } catch (OperationCanceledException ex) {
                        _logger.LogWarning(ex, "Background worker loop was cancelled.");
                    } catch (Exception ex) {
                        _logger.LogError(ex, "Error running background worker loop: {Message}", ex.Message);
                    }
                }
            }, _workLoopCancellationTokenSource.Token)
                .ContinueWith(t => {
                    if (t.IsFaulted) { 
                        var ex = t.Exception.InnerException; 
                        _logger.LogError(ex, "Background worker loop exiting: {Message}", ex.Message);
                    } else if (t.IsCanceled || _workLoopCancellationTokenSource.Token.IsCancellationRequested) { 
                        _logger.LogTrace("Background worker loop was cancelled."); 
                    } else { 
                        _logger.LogCritical("Background worker loop finished prematurely.");
                    }

                    if (!_workLoopCancellationTokenSource.Token.IsCancellationRequested)
                        StartWorking();
                });
        }

        public void Dispose() {
            _logger.LogTrace("Background task worker disposing");
            _workLoopCancellationTokenSource?.Cancel();
            _queue.Clear();
        }
    }
}