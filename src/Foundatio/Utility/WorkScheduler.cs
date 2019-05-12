using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    public class WorkScheduler : IDisposable {
        public static WorkScheduler Instance = new WorkScheduler();

        private readonly ILogger _logger;
        private bool _isDisposed = false;
        private readonly SortedQueue<DateTime, WorkItem> _workItems = new SortedQueue<DateTime, WorkItem>();
        private readonly TaskFactory _taskFactory;
        private Task _workLoopTask;
        private readonly object _lock = new object();

        public WorkScheduler(ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<WorkScheduler>() ?? NullLogger<WorkScheduler>.Instance;
            // limit scheduled task processing to 50 at a time
            _taskFactory = new TaskFactory(new LimitedConcurrencyLevelTaskScheduler(50));
        }

        public void Schedule(Func<Task> action, TimeSpan delay, TimeSpan? interval = null) {
            Schedule(() => { _ = action(); }, SystemClock.UtcNow.Add(delay), interval);
        }

        public void Schedule(Action action, TimeSpan delay, TimeSpan? interval = null) {
            Schedule(action, SystemClock.UtcNow.Add(delay), interval);
        }

        public void Schedule(Func<Task> action, DateTime executeAt, TimeSpan? interval = null) {
            Schedule(() => { _ = action(); }, executeAt, interval);
        }

        public void Schedule(Action action, DateTime executeAt, TimeSpan? interval = null) {
            EnsureWorkLoopRunning();

            if (executeAt.Kind != DateTimeKind.Utc)
                executeAt = executeAt.ToUniversalTime();

            _logger.LogTrace("Scheduling work due at {ExecuteAt}", executeAt);
            _workItems.Enqueue(executeAt, new WorkItem {
                Action = action,
                ExecuteAtUtc = executeAt,
                Interval = interval
            });
        }

        private void EnsureWorkLoopRunning() {
            if (_workLoopTask != null)
                return;

            lock (_lock) {
                if (_workLoopTask != null)
                    return;

                _logger.LogTrace("Starting work loop");
                _workLoopTask = Task.Factory.StartNew(WorkLoop, TaskCreationOptions.LongRunning);
            }
        }

        private void WorkLoop() {
            _logger.LogTrace("Work loop started");
            while (!_isDisposed) {
                _logger.LogTrace("Checking for items due after {CurrentTime}", SystemClock.UtcNow);
                if (_workItems.TryDequeueIf(out var kvp, i => i.ExecuteAtUtc < SystemClock.UtcNow)) {
                    _logger.LogTrace("Starting work item due at {DueTime}", kvp.Key);
                    _ = _taskFactory.StartNew(() => {
                        var startTime = SystemClock.UtcNow;
                        kvp.Value.Action();
                        if (kvp.Value.Interval.HasValue)
                            Schedule(kvp.Value.Action, startTime.Add(kvp.Value.Interval.Value));
                    });
                    _logger.LogTrace("Work item started");
                } else {
                    _logger.LogTrace("No work items due");
                    Thread.Sleep(100);
                }
            }
            _logger.LogTrace("Work loop stopped");
        }

        public void Dispose() {
            _isDisposed = true;
            _workLoopTask.Wait(5000);
            _workLoopTask = null;
        }

        private class WorkItem {
            public DateTime ExecuteAtUtc { get; set; }
            public Action Action { get; set; }
            public TimeSpan? Interval { get; set; }
        }
    }
}