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

        public void Schedule(Action action, DateTime executeAt, TimeSpan? interval = null) {
            EnsureWorkLoopRunning();

            _workItems.Enqueue(executeAt, new WorkItem {
                Action = action,
                ExecuteAtUtc = executeAt,
                Interval = interval
            });
        }

        public void Schedule(Func<Task> action, DateTime executeAt, TimeSpan? interval = null) {
            Schedule(() => { _ = action(); }, executeAt, interval);
        }

        private void EnsureWorkLoopRunning() {
            if (_workLoopTask != null)
                return;

            lock (_lock) {
                if (_workLoopTask != null)
                    return;

                _workLoopTask = Task.Factory.StartNew(WorkLoop, TaskCreationOptions.LongRunning);
            }
        }

        private void WorkLoop() {
            while (!_isDisposed) {
                if (!_workItems.TryPeek(out var kvp) || kvp.Key < SystemClock.UtcNow) {
                    Thread.Sleep(100);
                    continue;
                }

                while (!_isDisposed && _workItems.TryDequeue(out var i)) {
                    _ = _taskFactory.StartNew(() => {
                        var startTime = SystemClock.UtcNow;
                        i.Value.Action();
                        if (i.Value.Interval.HasValue)
                            Schedule(i.Value.Action, startTime.Add(i.Value.Interval.Value));
                    });
                }
            }
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