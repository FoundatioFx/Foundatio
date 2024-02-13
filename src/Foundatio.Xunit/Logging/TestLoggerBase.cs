using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public abstract class TestLoggerBase : IClassFixture<TestLoggerFixture>, IAsyncLifetime
{
    protected TestLoggerBase(ITestOutputHelper output, TestLoggerFixture fixture)
    {
        Fixture = fixture;
        fixture.Output = output;
        fixture.AddServiceRegistrations(RegisterServices);
    }

    protected TestLoggerFixture Fixture { get; }
    protected IServiceProvider Services => Fixture.Services;
    protected TestLogger TestLogger => Fixture.TestLogger;
    protected ILogger Log => Fixture.Log;

    protected virtual void RegisterServices(IServiceCollection services)
    {
    }

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        Fixture.TestLogger.Clear();
        return Task.CompletedTask;
    }
}
