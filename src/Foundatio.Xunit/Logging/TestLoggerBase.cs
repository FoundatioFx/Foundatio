using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public abstract class TestLoggerBase : IAsyncDisposable
{
    protected readonly ILogger _logger;
    protected readonly List<IDisposable> _disposables = [];

    protected TestLoggerBase(ITestOutputHelper output)
    {
        var services = new ServiceCollection();
        RegisterRequiredServices(services, output);
        var sp = services.BuildServiceProvider();
        _disposables.Add(sp);
        Services = sp;
        Log = Services.GetTestLogger();
        _logger = Log.CreateLogger(GetType());
    }

    protected IServiceProvider Services { get; }

    protected TService GetService<TService>() where TService : notnull
    {
        return Services.GetRequiredService<TService>();
    }

    protected TestLogger Log { get; }

    private void RegisterRequiredServices(IServiceCollection services, ITestOutputHelper output)
    {
        services.AddLogging(c => c.AddTestLogger(output));
        RegisterServices(services);
    }

    protected virtual void RegisterServices(IServiceCollection services)
    {
    }

    public virtual ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error disposing resource.");
            }
        }

        return new ValueTask(Task.CompletedTask);
    }
}
