using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Hosting.Startup {
    public interface IStartupAction {
        Task RunAsync(CancellationToken shutdownToken = default);
    }
}
