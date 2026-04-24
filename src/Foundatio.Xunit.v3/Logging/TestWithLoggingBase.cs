using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Xunit;

public abstract class TestWithLoggingBase : IAsyncLifetime
{
    private readonly CancellationTokenSource _testCancellationTokenSource =
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    protected readonly ILogger _logger;

    protected TestWithLoggingBase(ITestOutputHelper output)
    {
        Log = new TestLogger(output);
        _logger = Log.CreateLogger(GetType());
    }

    protected TestLogger Log { get; }

    /// <summary>
    /// Gets a cancellation token that is cancelled when the current test completes or
    /// when the test run is aborted/timed out. Pass this token to
    /// <see cref="Foundatio.Messaging.IMessageSubscriber.SubscribeAsync{T}"/>
    /// and other async operations to ensure automatic cleanup between tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This token is linked to <c>TestContext.Current.CancellationToken</c>, so it triggers on
    /// both per-test disposal (via <see cref="DisposeAsync"/>) and run-level abort or timeout.
    /// xUnit creates a new class instance per <c>[Fact]</c>, so each test gets a fresh linked token.
    /// </para>
    /// </remarks>
    protected CancellationToken TestCancellationToken => _testCancellationTokenSource.Token;

    public virtual ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        _testCancellationTokenSource.Cancel();
        _testCancellationTokenSource.Dispose();
        return ValueTask.CompletedTask;
    }
}
