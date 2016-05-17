using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Utility {
    public class MaintenanceBase : IDisposable {
        private DateTime _nextMaintenance = DateTime.MaxValue;
        private Timer _maintenanceTimer;
        protected readonly ILogger _logger;

        public MaintenanceBase(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger(GetType());
        }

        protected void InitializeMaintenance(TimeSpan? dueTime = null, TimeSpan? intervalTime = null) {
            int dueTimeMs = dueTime.HasValue ? (int)dueTime.Value.TotalMilliseconds : Timeout.Infinite;
            int intervalTimeMs = intervalTime.HasValue ? (int)intervalTime.Value.TotalMilliseconds : Timeout.Infinite;
            _maintenanceTimer = new Timer(s => DoMaintenanceInternalAsync().GetAwaiter().GetResult(), null, dueTimeMs, intervalTimeMs);
        }

        protected void ScheduleNextMaintenance(DateTime utcDate) {
            _logger.Trace("ScheduleNextMaintenance called: value={value}", utcDate);

            if (utcDate == DateTime.MaxValue)
                return;

            if (_nextMaintenance < DateTime.UtcNow)
                _nextMaintenance = DateTime.MaxValue;

            if (utcDate > _nextMaintenance)
                return;

            int delay = Math.Max((int)utcDate.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = utcDate;
            _logger.Trace("Scheduling maintenance: delay={delay}", delay);

            _maintenanceTimer.Change(delay, Timeout.Infinite);
        }

        private async Task DoMaintenanceInternalAsync() {
            _logger.Trace("DoMaintenanceAsync");
            ScheduleNextMaintenance(await DoMaintenanceAsync().AnyContext());
        }

        protected virtual Task<DateTime> DoMaintenanceAsync() {
            return Task.FromResult(DateTime.MaxValue);
        }
        
        public virtual void Dispose() {
            _maintenanceTimer?.Dispose();
        }
    }
}
