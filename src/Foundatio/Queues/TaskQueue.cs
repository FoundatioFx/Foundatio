using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Queues {
    public class TaskQueue: IDisposable {
        private readonly ConcurrentQueue<Func<Task>> _queue = new ConcurrentQueue<Func<Task>>();
        private readonly ConcurrentDictionary<int, Task> _workers = new ConcurrentDictionary<int, Task>();
        private readonly AsyncAutoResetEvent _autoResetEvent = new AsyncAutoResetEvent();

        private TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>();
        private readonly CancellationTokenSource _disposedCancellationTokenSource;
        private readonly int _maxItems;
        private readonly int _maxDegreeOfParallelism;
        private int _working;
        private readonly ILogger _logger;

        public TaskQueue(int maxItems = Int32.MaxValue, byte maxDegreeOfParallelism = 1, ILoggerFactory loggerFactory = null) {
            _maxItems = maxItems;
            _maxDegreeOfParallelism = maxDegreeOfParallelism;
            _logger = loggerFactory?.CreateLogger<TaskQueue>() ?? NullLogger<TaskQueue>.Instance;
            _disposedCancellationTokenSource = new CancellationTokenSource();
        }

        public int Queued => _queue.Count;
        public int Workers => _workers.Count;
        public int Working => _working;

        public bool Enqueue(Func<Task> task) {
            if (_queue.Count >= _maxItems) {
                _logger.LogError("Ignoring queued task: Queue is full");
                return false;
            }

            _queue.Enqueue(task);
            _autoResetEvent.Set();
            return true;
        }

        public Task RunAsync(CancellationToken token = default(CancellationToken)) {
            return RunImplAsync(false, token);
        }

        public void RunContinuous(CancellationToken token = default(CancellationToken)) {
            _logger.LogInformation("Running continuous task queue");
            Task.Run(() => RunImplAsync(true, token), GetLinkedDisposableCanncellationToken(token))
                .ContinueWith(t => {
                    if (t.IsFaulted) {
                        var ex = t.Exception.InnerException;
                        _logger.LogError(ex, "Error executing task queue: {Message}", ex.Message);
                    } else if (t.IsCanceled) {
                        _logger.LogWarning("Task queue was cancelled.");
                    } else {
                        _logger.LogError("Continous task queue execution finished and will retry.");
                        RunContinuous(token);
                    }
                }
            );
        }

        private Task RunImplAsync(bool isContinous, CancellationToken token) {
            //var previous = _completion;
            //RunWithTaskCompletionImpl(isContinous, GetLinkedDisposableCanncellationToken(token));
            //return previous.Task;

            return RunWithWhenAnyAsync(isContinous, token);
        }

        private void RunWithTaskCompletionImpl(bool isContinous, CancellationToken token) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            int tasksToStart = _maxDegreeOfParallelism - _workers.Count;
            for (int id = 1; id <= tasksToStart; id++) {
                if (token.IsCancellationRequested) {
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Cancellation requested.");
                    break;
                }

                if (isTraceLogLevelEnabled) _logger.LogTrace("Starting task: {TaskId} of {MaxDegreeOfParallelism} ", id, _maxDegreeOfParallelism);
                var unawaitedTask = Task.Run(() => DequeueAsync(isContinous, token), token);
                if (!_workers.TryAdd(unawaitedTask.GetHashCode(), unawaitedTask))
                    throw new InvalidOperationException("A task with this hash has already been added.");

                int taskId = id;
                unawaitedTask.ContinueWith(t => {
                    if (!_workers.TryRemove(t.GetHashCode(), out _))
                        throw new InvalidOperationException("A task with this hash has already been removed.");

                    if (t.IsFaulted) {
                        var ex = t.Exception.InnerException;
                        _logger.LogError(ex, "Error executing task: {Message}", ex.Message);
                    } else if (t.IsCanceled) {
                        _logger.LogWarning("Task was cancelled.");
                    } else if (isTraceLogLevelEnabled) {
                        _logger.LogTrace("Finished task: {TaskId} of {MaxDegreeOfParallelism} ", taskId, _maxDegreeOfParallelism);
                    }

                    if (!token.IsCancellationRequested && (isContinous || _workers.IsEmpty && !_queue.IsEmpty)) {
                        if (isTraceLogLevelEnabled) _logger.LogTrace("Restarting Tasks: Continous={Continous}, Continous={Continous} Queued={QueueCount}", isContinous, _workers.Count);
                        RunWithTaskCompletionImpl(isContinous, token);
                        return;
                    }

                    if (!isContinous && _workers.IsEmpty) {
                        var previousCompletionSource = Interlocked.Exchange(ref _completion, new TaskCompletionSource<bool>());
                        if (token.IsCancellationRequested)
                            previousCompletionSource.TrySetCanceled(token);
                        else
                            previousCompletionSource.TrySetResult(true);
                    }
                });
            }
        }

        private async Task RunWithWhenAnyAsync(bool isContinous, CancellationToken token) {
            void AddWorkerTask(bool logTraceMessage, CancellationToken cancellationToken) {
                var unawaitedTask = Task.Run(() => DequeueAsync(isContinous, cancellationToken), cancellationToken);
                if (!_workers.TryAdd(unawaitedTask.GetHashCode(), unawaitedTask))
                    throw new InvalidOperationException("A task with this hash has already been added.");

                if (logTraceMessage) 
                    _logger.LogTrace("Started task: {TaskId} of {MaxDegreeOfParallelism} ", _workers.Count, _maxDegreeOfParallelism);
            }

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            var linkedDisposableCanncellationToken = GetLinkedDisposableCanncellationToken(token);
            if (linkedDisposableCanncellationToken.IsCancellationRequested) {
                if (isTraceLogLevelEnabled) _logger.LogTrace("Cancellation requested.");
                return;
            }

            int tasksToStart = _maxDegreeOfParallelism - _workers.Count;
            for (int i = 0; i < tasksToStart; i++)
                AddWorkerTask(isTraceLogLevelEnabled, linkedDisposableCanncellationToken);

            while (!_workers.IsEmpty) {
                var completedDequeueTask = await Task.WhenAny(_workers.Values).AnyContext();
                if (!_workers.TryRemove(completedDequeueTask.GetHashCode(), out _))
                    throw new InvalidOperationException("A task with this hash has already been removed.");

                bool isFaulted = completedDequeueTask.IsFaulted;
                if (isFaulted) {
                    var ex = completedDequeueTask.Exception.InnerException;
                    _logger.LogError(ex, "Error running dequeue task: {Message}", ex.Message);
                } else if (completedDequeueTask.IsCanceled) {
                    _logger.LogWarning("Dequeue task was cancelled.");
                } else if (isTraceLogLevelEnabled) {
                    _logger.LogTrace("Finished running dequeued task.");
                }

                if (linkedDisposableCanncellationToken.IsCancellationRequested)
                    continue;

                if (isContinous || isFaulted || _workers.IsEmpty && !_queue.IsEmpty) {
                    if (isTraceLogLevelEnabled) 
                        _logger.LogTrace("Restarting Task: Continous={Continous} Queued={QueueCount}", isContinous, _workers.Count);

                    AddWorkerTask(isTraceLogLevelEnabled, linkedDisposableCanncellationToken);
                }
            }
        }

        private async Task DequeueAsync(bool isContinous, CancellationToken linkedCancellationToken) {
            while (!linkedCancellationToken.IsCancellationRequested) {
                var action = await DequeueImplAsync(isContinous, linkedCancellationToken).AnyContext();
                if (action == null && isContinous || linkedCancellationToken.IsCancellationRequested)
                    continue;

                if (action == null)
                    break;

                try {
                    Interlocked.Increment(ref _working);
                    await action().AnyContext();
                } catch (OperationCanceledException ex) {
                    _logger.LogWarning(ex, "Task was cancelled.");
                } catch (Exception ex) {
                    _logger.LogError(ex, "Error executing task: {Message}", ex.Message);
                } finally {
                    Interlocked.Decrement(ref _working);
                }
            }
        }

        private async Task<Func<Task>> DequeueImplAsync(bool isContinous, CancellationToken linkedCancellationToken) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Dequeuing item... Queue count: {Count}", _queue.Count);

            while (isContinous && !linkedCancellationToken.IsCancellationRequested && _queue.Count == 0) {
                if (isTraceLogLevelEnabled) _logger.LogTrace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    await _autoResetEvent.WaitAsync(GetDequeueCanncellationToken(linkedCancellationToken)).AnyContext();
                } catch (OperationCanceledException) { }

                sw.Stop();
                if (isTraceLogLevelEnabled) _logger.LogTrace("Waited for dequeue: {Elapsed:g}", sw.Elapsed);
            }

            if (linkedCancellationToken.IsCancellationRequested || _queue.Count == 0)
                return null;

            if (isTraceLogLevelEnabled) _logger.LogTrace("Returning dequeued item...");
            return _queue.TryDequeue(out var task) ? task : null;
        }

        private CancellationToken GetDequeueCanncellationToken(CancellationToken linkedDisposedCancellationToken) {
            if (linkedDisposedCancellationToken.IsCancellationRequested)
                return linkedDisposedCancellationToken;

            return CancellationTokenSource.CreateLinkedTokenSource(linkedDisposedCancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).Token;
        }

        private CancellationToken GetLinkedDisposableCanncellationToken(CancellationToken cancellationToken) {
            if (cancellationToken.IsCancellationRequested)
                return cancellationToken;

            return CancellationTokenSource.CreateLinkedTokenSource(_disposedCancellationTokenSource.Token, cancellationToken).Token;
        }

        public void Dispose() {
            _disposedCancellationTokenSource.Cancel();
            _queue.Clear();
        }
    }
}