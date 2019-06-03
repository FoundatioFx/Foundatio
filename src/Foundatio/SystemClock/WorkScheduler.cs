using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    /// <summary>
    /// Used for scheduling tasks to be completed in the future. Uses the SystemClock so that making use of this makes it easy to test time sensitive code.
    /// <remarks>This is the same as using the thread pool. Long running tasks should not be scheduled on this. Tasks should generally last no longer than a few seconds.</remarks>
    /// </summary>
    public class WorkScheduler : IDisposable {
        public static readonly WorkScheduler Default = new WorkScheduler(SystemClock.Instance);

        private ILogger _logger;
        private bool _isDisposed = false;
        private readonly SortedQueue<DateTime, WorkItem> _workItems = new SortedQueue<DateTime, WorkItem>();
        private readonly TaskFactory _taskFactory;
        private Task _workLoopTask;
        private readonly object _lock = new object();
        private readonly AutoResetEvent _workItemScheduled = new AutoResetEvent(false);
        private readonly ISystemClock _clock;
        private readonly AutoResetEvent _noWorkItemsDue = new AutoResetEvent(false);

        public WorkScheduler(ISystemClock clock, ILogger logger = null) {
            _clock = clock;
            _logger = logger ?? NullLogger.Instance;
            // limit scheduled task processing to 50 at a time
            _taskFactory = new TaskFactory(new LimitedConcurrencyLevelTaskScheduler(50));
        }
        
        public WaitHandle NoWorkItemsDue => _noWorkItemsDue;

        public void Schedule(Action action, TimeSpan delay, TimeSpan? interval = null) {
            Schedule(action, _clock.UtcNow.Add(delay), interval);
        }

        public void Schedule(Action action, DateTime executeAt, TimeSpan? interval = null) {
            if (executeAt.Kind != DateTimeKind.Utc)
                executeAt = executeAt.ToUniversalTime();

            var delay = executeAt.Subtract(_clock.UtcNow);
            _logger.LogTrace("Scheduling work due at {ExecuteAt} ({Delay:g} from now)", executeAt, delay);
            _workItems.Enqueue(executeAt, new WorkItem {
                Action = action,
                ExecuteAtUtc = executeAt,
                Interval = interval
            });

            EnsureWorkLoopRunning();
            _workItemScheduled.Set();
        }

        private void EnsureWorkLoopRunning() {
            if (_workLoopTask != null)
                return;

            lock (_lock) {
                if (_workLoopTask != null)
                    return;

                _logger.LogTrace("Starting work loop");
                if (_clock is ITestSystemClock testClock)
                    testClock.Changed += (s, e) => { _workItemScheduled.Set(); };
                
                _workLoopTask = Task.Factory.StartNew(WorkLoop, TaskCreationOptions.LongRunning);
            }
        }

        private void WorkLoop() {
            _logger.LogTrace("Work loop started");
            while (!_isDisposed) {
                if (_workItems.TryDequeueIf(out var kvp, i => i.ExecuteAtUtc < _clock.UtcNow)) {
                    _logger.LogTrace("Starting work item due at {DueTime} current time {CurrentTime}", kvp.Key, _clock.UtcNow);
                    _ = _taskFactory.StartNew(() => {
                        var startTime = _clock.UtcNow;
                        kvp.Value.Action();
                        if (kvp.Value.Interval.HasValue)
                            Schedule(kvp.Value.Action, startTime.Add(kvp.Value.Interval.Value));
                    });
                    continue;
                }
                
                _noWorkItemsDue.Set();
                
                if (kvp.Key != default) {
                    var delay = kvp.Key.Subtract(_clock.UtcNow);
                    _logger.LogTrace("No work items due, next due at {DueTime} ({Delay:g} from now)", kvp.Key, delay);
                    _workItemScheduled.WaitOne(delay);
                } else {
                    _logger.LogTrace("No work items scheduled");
                    _workItemScheduled.WaitOne(TimeSpan.FromMinutes(1));
                }
            }
            _logger.LogTrace("Work loop stopped");
        }

        public void Dispose() {
            _isDisposed = true;
            _workLoopTask.Wait();
            _workLoopTask = null;
        }

        private class WorkItem {
            public DateTime ExecuteAtUtc { get; set; }
            public Action Action { get; set; }
            public TimeSpan? Interval { get; set; }
        }
    }
}