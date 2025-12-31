using System.Threading;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Xunit;

public abstract class TestWithLoggingBase
{
    protected readonly ILogger _logger;

    protected TestWithLoggingBase(ITestOutputHelper output)
    {
        Log = new TestLogger(output);
        _logger = Log.CreateLogger(GetType());
    }

    protected TestLogger Log { get; }

    /// <summary>
    /// Gets the cancellation token for the current test. This token is automatically cancelled when the test times out.
    /// </summary>
    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;
}
