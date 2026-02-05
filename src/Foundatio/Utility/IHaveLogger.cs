using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

/// <summary>
/// Indicates that a type exposes a logger for diagnostic output.
/// </summary>
public interface IHaveLogger
{
    /// <summary>
    /// Gets the logger for this instance.
    /// </summary>
    ILogger Logger { get; }
}

/// <summary>
/// Indicates that a type exposes a logger factory for creating loggers.
/// </summary>
public interface IHaveLoggerFactory
{
    /// <summary>
    /// Gets the logger factory for creating loggers.
    /// </summary>
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
