using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    public class RealSystemClock : ISystemClock {
        private readonly WorkScheduler _workScheduler;
        
        public RealSystemClock(ILoggerFactory loggerFactory) {
            loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            var logger = loggerFactory.CreateLogger<ISystemClock>();
            _workScheduler = new WorkScheduler(this, logger);
        }
        
        public DateTime Now => DateTime.Now;
        public DateTime UtcNow => DateTime.UtcNow;
        public DateTimeOffset OffsetNow => DateTimeOffset.Now;
        public DateTimeOffset OffsetUtcNow => DateTimeOffset.UtcNow;
        public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
        public Task SleepAsync(int milliseconds, CancellationToken ct = default) => Task.Delay(milliseconds, ct);
        public TimeSpan Offset => DateTimeOffset.Now.Offset;
        public void Schedule(Action action, TimeSpan dueTime)
            => _workScheduler.Schedule(action, dueTime);
        public ITimer Timer(Action action, TimeSpan dueTime, TimeSpan period)
            => _workScheduler.Timer(action, dueTime, period);
    }
}