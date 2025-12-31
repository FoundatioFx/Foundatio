using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Xunit;

public abstract class TestLoggerBase : IClassFixture<TestLoggerFixture>, IAsyncLifetime
{
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

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        Fixture.TestLogger.Reset();
        return ValueTask.CompletedTask;
    }
}
