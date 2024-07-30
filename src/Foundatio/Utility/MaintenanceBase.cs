﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

public class MaintenanceBase : IDisposable
{
    private ScheduledTimer _maintenanceTimer;
    private readonly ILoggerFactory _loggerFactory;
    protected readonly TimeProvider _timeProvider;
    protected readonly ILogger _logger;

    public MaintenanceBase(TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
    }

    protected void InitializeMaintenance(TimeSpan? dueTime = null, TimeSpan? intervalTime = null)
    {
        _maintenanceTimer = new ScheduledTimer(DoMaintenanceAsync, dueTime, intervalTime, _timeProvider, _loggerFactory);
    }

    protected void ScheduleNextMaintenance(DateTimeOffset utcDate)
    {
        _maintenanceTimer.ScheduleNext(utcDate);
    }

    protected virtual Task<DateTimeOffset?> DoMaintenanceAsync()
    {
        return Task.FromResult<DateTimeOffset?>(DateTimeOffset.MaxValue);
    }

    public virtual void Dispose()
    {
        _maintenanceTimer?.Dispose();
    }
}
