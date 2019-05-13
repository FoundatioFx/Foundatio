using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    public class WorkScheduler : IDisposable {
        public static WorkScheduler Default = new WorkScheduler();

        private ILogger _logger;
        private bool _isDisposed = false;
        private readonly SortedQueue<DateTime, WorkItem> _workItems = new SortedQueue<DateTime, WorkItem>();
        private readonly TaskFactory _taskFactory;
        private Task _workLoopTask;
        private readonly object _lock = new object();
        private readonly AutoResetEvent _workItemScheduled = new AutoResetEvent(false);

        public WorkScheduler(ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<WorkScheduler>() ?? NullLogger<WorkScheduler>.Instance;
            // limit scheduled task processing to 50 at a time
            _taskFactory = new TaskFactory(new LimitedConcurrencyLevelTaskScheduler(50));
        }

        public void SetLogger(ILogger logger) {
            _logger = logger ?? NullLogger.Instance;
        }

        public void SetLogger(ILoggerFactory loggerFactory) {
            _logger = loggerFactory?.CreateLogger<WorkScheduler>() ?? NullLogger<WorkScheduler>.Instance;
        }

        public AutoResetEvent NoWorkItemsDue { get; } = new AutoResetEvent(false);

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

            var delay = executeAt.Subtract(SystemClock.UtcNow);
            _logger.LogTrace("Scheduling work due at {ExecuteAt} ({Delay:g} from now)", executeAt, delay);
            _workItems.Enqueue(executeAt, new WorkItem {
                Action = action,
                ExecuteAtUtc = executeAt,
                Interval = interval
            });
            _workItemScheduled.Set();
        }

        private void EnsureWorkLoopRunning() {
            if (_workLoopTask != null)
                return;

            lock (_lock) {
                if (_workLoopTask != null)
                    return;

                _logger.LogTrace("Starting work loop");
                TestSystemClock.Changed += (s, e) => { _workItemScheduled.Set(); };
                _workLoopTask = Task.Factory.StartNew(WorkLoop, TaskCreationOptions.LongRunning);
            }
        }

        private void WorkLoop() {
            _logger.LogTrace("Work loop started");
            while (!_isDisposed) {
                if (_workItems.TryDequeueIf(out var kvp, i => i.ExecuteAtUtc < SystemClock.UtcNow)) {
                    _logger.LogTrace("Starting work item due at {DueTime} current time {CurrentTime}", kvp.Key, SystemClock.UtcNow);
                    _ = _taskFactory.StartNew(() => {
                        var startTime = SystemClock.UtcNow;
                        kvp.Value.Action();
                        if (kvp.Value.Interval.HasValue)
                            Schedule(kvp.Value.Action, startTime.Add(kvp.Value.Interval.Value));
                    });
                    continue;
                }

                if (kvp.Key != default) {
                    var delay = kvp.Key.Subtract(SystemClock.UtcNow);
                    _logger.LogTrace("No work items due, next due at {DueTime} ({Delay:g} from now)", kvp.Key, delay);
                    
                    // this can happen if items were inserted right after the loop started
                    if (delay < TimeSpan.Zero)
                        continue;
                    
                    NoWorkItemsDue.Set();
                    
                    // don't delay more than 1 minute
                    // TODO: Do we really need this? We know when items are enqueued. I think we can trust it and wait the full time.
                    if (delay > TimeSpan.FromMinutes(1))
                        delay = TimeSpan.FromMinutes(1);
                    _workItemScheduled.WaitOne(delay);
                } else {
                    _logger.LogTrace("No work items scheduled");
                    NoWorkItemsDue.Set();
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