using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

public interface IHaveLogger
{
    ILogger Logger { get; }
}

public interface IHaveLoggerFactory
{
    ILoggerFactory LoggerFactory { get; }
}

public static class LoggerExtensions
{
    public static ILogger GetLogger(this object target)
    {
        return target is IHaveLogger accessor ? accessor.Logger ?? NullLogger.Instance : NullLogger.Instance;
    }

    public static ILoggerFactory GetLoggerFactory(this object target)
    {
        return target is IHaveLoggerFactory accessor ? accessor.LoggerFactory ?? NullLoggerFactory.Instance : NullLoggerFactory.Instance;
    }
}
