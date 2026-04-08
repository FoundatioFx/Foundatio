using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Xunit;

[ProviderAlias("Test")]
public class TestLoggerProvider : ILoggerProvider
{
    public TestLoggerProvider(TestLoggerOptions? options = null)
    {
        Log = new TestLogger(options ?? new TestLoggerOptions());
    }

    public TestLogger Log { get; }

    public virtual ILogger CreateLogger(string categoryName)
    {
        return Log.CreateLogger(categoryName);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    ~TestLoggerProvider()
    {
        Dispose(false);
    }
}
