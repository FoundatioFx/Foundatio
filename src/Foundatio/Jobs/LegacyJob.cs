using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Legacy;

/// <summary>
/// The legacy job contract, run once or continuously by the legacy <see cref="JobRunner"/> and hosted runners.
/// Superseded by the durable-runtime <see cref="Foundatio.Jobs.IJob"/> (which is handed a
/// <see cref="Foundatio.Jobs.JobExecutionContext"/> per run); kept for compatibility.
/// </summary>
public interface IJob
{
    Task<JobResult> RunAsync(CancellationToken cancellationToken = default);
}
