using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Utility {
    public class MaintenanceBase : IDisposable {
        private DateTime _nextMaintenance = DateTime.MaxValue;
        private Timer _maintenanceTimer;

        protected void InitializeMaintenance() {
            _maintenanceTimer = new Timer(async s => await DoMaintenanceInternalAsync().AnyContext(), null, Timeout.Infinite, Timeout.Infinite);
        }

        protected void ScheduleNextMaintenance(DateTime value) {
#if DEBUG
            Logger.Trace().Message($"ScheduleNextMaintenance: value={value}").Write();
#endif
            if (value == DateTime.MaxValue)
                return;

            if (_nextMaintenance < DateTime.UtcNow)
                _nextMaintenance = DateTime.MaxValue;

            if (value > _nextMaintenance)
                return;

            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = value;
#if DEBUG
            Logger.Trace().Message($"Scheduling maintenance: delay={delay}").Write();
#endif
            _maintenanceTimer.Change(delay, Timeout.Infinite);
        }

        private async Task DoMaintenanceInternalAsync() {
#if DEBUG
            Logger.Trace().Message("DoMaintenanceAsync").Write();
#endif
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
