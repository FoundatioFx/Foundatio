using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public abstract class TestLoggerBase : IClassFixture<TestLoggerFixture>, IAsyncLifetime
{
    private readonly CancellationTokenSource _testCancellationTokenSource = new();

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
    /// Gets a cancellation token that is cancelled when the current test completes.
    /// Pass this token to <see cref="Foundatio.Messaging.IMessageSubscriber.SubscribeAsync{T}"/>
    /// and other async operations to ensure automatic cleanup between tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This token is cancelled in <see cref="DisposeAsync"/> after each test.
    /// xUnit creates a new class instance per <c>[Fact]</c>, so each test gets a fresh token.
    /// </para>
    /// <para>
    /// In Foundatio.Xunit.v3, this property uses a linked token source that also
    /// respects <c>TestContext.Current.CancellationToken</c> for run-level abort and timeout.
    /// </para>
    /// </remarks>
    protected CancellationToken TestCancellationToken => _testCancellationTokenSource.Token;

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        _testCancellationTokenSource.Cancel();
        _testCancellationTokenSource.Dispose();
        Fixture.TestLogger.Reset();
        return Task.CompletedTask;
    }
}
