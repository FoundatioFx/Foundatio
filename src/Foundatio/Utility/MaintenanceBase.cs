using System;
using System.Threading.Tasks;
using Foundatio.Logging;

namespace Foundatio.Utility {
    public class MaintenanceBase : IDisposable {
        private ScheduledTimer _maintenanceTimer;
        private readonly ILoggerFactory _loggerFactory;
        protected readonly ILogger _logger;

        public MaintenanceBase(ILoggerFactory loggerFactory) {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger(GetType());
        }

        protected void InitializeMaintenance(TimeSpan? dueTime = null, TimeSpan? intervalTime = null) {
            _maintenanceTimer = new ScheduledTimer(DoMaintenanceAsync, dueTime, intervalTime, _loggerFactory);
        }

        protected void ScheduleNextMaintenance(DateTime utcDate) {
            _maintenanceTimer.ScheduleNext(utcDate);
        }

        protected virtual Task<DateTime?> DoMaintenanceAsync() {
            return Task.FromResult<DateTime?>(DateTime.MaxValue);
        }
        
        public virtual void Dispose() {
            _maintenanceTimer?.Dispose();
        }
    }
}
