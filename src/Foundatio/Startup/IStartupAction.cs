using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Startup {
    public interface IStartupAction {
        Task RunAsync(CancellationToken shutdownToken = default);
    }
}
