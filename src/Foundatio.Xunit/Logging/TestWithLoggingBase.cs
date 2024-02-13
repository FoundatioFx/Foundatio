using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

public abstract class TestWithLoggingBase
{
    protected readonly ILogger _logger;

    protected TestWithLoggingBase(ITestOutputHelper output)
    {
        Log = output.ToTestLogger();
        _logger = Log.CreateLogger(GetType());
    }

    protected TestLogger Log { get; }
}
