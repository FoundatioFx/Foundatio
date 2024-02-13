using System;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Xunit;

[Obsolete("Use TestLoggerBase instead.")]
public abstract class TestWithLoggingBase
{
    protected readonly ILogger _logger;

    protected TestWithLoggingBase(ITestOutputHelper output)
    {
        Log = new TestLogger(output);
        _logger = Log.CreateLogger(GetType());
    }

    protected TestLogger Log { get; }
}
