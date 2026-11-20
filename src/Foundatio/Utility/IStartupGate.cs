using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility;

/// <summary>
/// Background processing (message handlers, the job worker and the job scheduler) waits for this before starting,
/// so work never runs before startup tasks such as database migrations have finished. Foundatio.Extensions.Hosting
/// registers one automatically for startup actions.
/// </summary>
public interface IStartupGate
{
    /// <summary>Returns true when processing may start, or false when startup failed and processing must not start.</summary>
    Task<bool> WaitAsync(CancellationToken cancellationToken);
}
