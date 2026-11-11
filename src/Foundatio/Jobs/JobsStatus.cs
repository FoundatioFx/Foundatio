using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>
/// What this process's job worker and CRON scheduler are doing. Registered by <c>AddFoundatioJobs</c>;
/// Foundatio.Extensions.Hosting turns it into a health check.
/// </summary>
public sealed class JobsStatus
{
    private const string StartupFailed = "Startup failed; jobs will not run";

    private readonly JobsRuntime _runtime;

    internal JobsStatus(JobsRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// The <c>worker</c> when job types are registered, and the <c>scheduler</c> when CRON jobs are. Empty when this
    /// process only enqueues. Both are <see cref="ProcessingState.Starting"/> until the startup gate opens.
    /// </summary>
    public IReadOnlyList<ProcessingStatus> GetComponents()
    {
        var result = new List<ProcessingStatus>(2);
        var hostState = _runtime.HostState;

        if (_runtime.Jobs.Count > 0)
        {
            var idle = _runtime.Jobs.Keys.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
            result.Add(_runtime.Worker?.GetStatus() ?? NotStarted("worker", hostState) with { ActiveByName = idle });
        }

        if (_runtime.Registrations.Schedules.Count > 0 && _runtime.RunSchedules)
            result.Add(_runtime.Scheduler?.GetStatus() ?? NotStarted("scheduler", hostState));

        return result;
    }

    private static ProcessingStatus NotStarted(string name, ProcessingState hostState) => new()
    {
        Name = name,
        State = hostState == ProcessingState.Running ? ProcessingState.Starting : hostState,
        LastError = hostState == ProcessingState.Faulted ? StartupFailed : null
    };
}
