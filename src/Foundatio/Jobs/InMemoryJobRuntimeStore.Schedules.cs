using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

public sealed partial class InMemoryJobRuntimeStore
{
    private readonly InMemoryScheduledJobStore _schedules = new();

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
        => _schedules.ScheduleAsync(definition, cancellationToken);
    public Task ReconcileAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
        => _schedules.ReconcileAsync(definition, cancellationToken);
    public Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default)
        => _schedules.GetScheduleAsync(name, cancellationToken);
    public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default)
        => _schedules.UnscheduleAsync(name, cancellationToken);
    public Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken cancellationToken = default)
        => _schedules.GetSchedulesAsync(query, cancellationToken);
}
