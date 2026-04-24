using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Xunit;

public abstract class TestLoggerBase : IClassFixture<TestLoggerFixture>, IAsyncLifetime
{
    private readonly CancellationTokenSource _testCancellationTokenSource =
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    protected TestLoggerBase(ITestOutputHelper output, TestLoggerFixture fixture)
    {
        Fixture = fixture;
        fixture.Output = output;
        fixture.ConfigureServices(ConfigureServices);
    }

    protected TestLoggerFixture Fixture { get; }
    protected IServiceProvider Services => Fixture.Services;
    protected TestLogger TestLogger => Fixture.TestLogger;
    protected ILogger Log => Fixture.Log;

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

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        _testCancellationTokenSource.Cancel();
        _testCancellationTokenSource.Dispose();
        Fixture.TestLogger.Reset();
        return ValueTask.CompletedTask;
    }
}
