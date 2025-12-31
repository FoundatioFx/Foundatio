using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Xunit2;

public class TestLoggerFixture : IAsyncLifetime
{
    private readonly List<object> _disposables = [];
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

    public void ConfigureServices(Action<IServiceCollection> registerServices)
    {
        _serviceRegistrations.Add(registerServices);
    }

    public IServiceProvider Services => _serviceProvider.Value;
    public TestLogger TestLogger => _testLogger.Value;
    public ILogger Log => _log.Value;

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(c => c.AddTestLogger(() => Output));
        foreach (var registration in _serviceRegistrations)
            registration(services);
    }

    protected virtual IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        var sp = services.BuildServiceProvider();
        _disposables.Add(sp);
        return sp;
    }

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        foreach (object disposable in _disposables)
        {
            try
            {
                switch (disposable)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case IDisposable syncDisposable:
                        syncDisposable.Dispose();
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                // Resource was already disposed; safe to ignore during cleanup
            }
            catch (Exception ex)
            {
                Log?.LogError(ex, "Error disposing resource.");
            }
        }
    }
}
