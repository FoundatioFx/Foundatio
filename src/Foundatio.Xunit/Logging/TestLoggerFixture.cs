using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public class TestLoggerFixture : IAsyncLifetime
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<Action<IServiceCollection>> _serviceRegistrations = [];
    private readonly Lazy<IServiceProvider> _serviceProvider;
    private readonly Lazy<TestLogger> _testLogger;
    private readonly Lazy<ILogger> _log;

    public TestLoggerFixture()
    {
        _serviceProvider = new Lazy<IServiceProvider>(BuildServiceProvider);
        _testLogger = new Lazy<TestLogger>(() => Services.GetTestLogger());
        _log = new Lazy<ILogger>(() => Services.GetRequiredService<ILoggerFactory>().CreateLogger(GetType()));
    }

    public ITestOutputHelper Output { get; set; }

    public void AddServiceRegistrations(Action<IServiceCollection> registerServices)
    {
        _serviceRegistrations.Add(registerServices);
    }

    public IServiceProvider Services => _serviceProvider.Value;

    public TestLogger TestLogger => _testLogger.Value;
    public ILogger Log => _log.Value;

    protected virtual void RegisterServices(IServiceCollection services)
    {
        if (Output == null)
            throw new InvalidOperationException("Output should be set before registering services.");

        services.AddLogging(c => c.AddTestLogger(Output));
        foreach (var registration in _serviceRegistrations)
            registration(services);
    }

    protected virtual IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        RegisterServices(services);
        var sp = services.BuildServiceProvider();
        _disposables.Add(sp);
        return sp;
    }

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Log?.LogError(ex, "Error disposing resource.");
            }
        }

        return Task.CompletedTask;
    }
}
