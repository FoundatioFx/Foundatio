using System.Threading;
using System.Threading.Tasks;
using Foundatio.Hosting.Startup;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample {
    public class MyStartupAction : IStartupAction {
        private readonly ILogger _logger;

        public MyStartupAction(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<MyStartupAction>();
        }

        public async Task RunAsync(CancellationToken cancellationToken = default) {
            for (int i = 0; i < 5; i++) {
                _logger.LogTrace("MyStartupAction Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
                await Task.Delay(500);
            }
        }
    }
}
