using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Extensions.Logging;

public class LogState : IEnumerable<KeyValuePair<string, object>>
{
    private readonly Dictionary<string, object> _state = new();

    public int Count => _state.Count;

    public object this[string property]
    {
        get { return _state[property]; }
        set { _state[property] = value; }
    }

    public LogState Property(string property, object value)
    {
        _state.Add(property, value);
        return this;
    }

    public LogState PropertyIf(string property, object value, bool condition)
    {
        if (condition)
            _state.Add(property, value);
        return this;
    }

    public bool ContainsProperty(string property)
    {
        return _state.ContainsKey(property);
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        return _state.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public static class LoggerExtensions
{
    public static IDisposable BeginScope(this ILogger logger, Func<LogState, LogState> stateBuilder)
    {
        var logState = new LogState();
        logState = stateBuilder(logState);
        return logger.BeginScope(logState);
    }

    public static IDisposable BeginScope(this ILogger logger, string property, object value)
    {
        return logger.BeginScope(b => b.Property(property, value));
    }

    public static void LogDebug(this ILogger logger, Func<LogState, LogState> stateBuilder, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogDebug(message, args);
    }

    public static void LogTrace(this ILogger logger, Func<LogState, LogState> stateBuilder, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogTrace(message, args);
    }

    public static void LogInformation(this ILogger logger, Func<LogState, LogState> stateBuilder, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogInformation(message, args);
    }

    public static void LogWarning(this ILogger logger, Func<LogState, LogState> stateBuilder, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogWarning(message, args);
    }

    public static void LogError(this ILogger logger, Func<LogState, LogState> stateBuilder, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogError(message, args);
    }

    public static void LogError(this ILogger logger, Func<LogState, LogState> stateBuilder, Exception exception, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogError(exception, message, args);
    }

    public static void LogCritical(this ILogger logger, Func<LogState, LogState> stateBuilder, string message, params object[] args)
    {
        using (BeginScope(logger, stateBuilder))
            logger.LogCritical(message, args);
    }

    public static LogState Critical(this LogState builder, bool isCritical = true)
    {
        return isCritical ? builder.Tag("Critical") : builder;
    }

    public static LogState Tag(this LogState builder, string tag)
    {
        return builder.Tag(new[] { tag });
    }

    public static LogState Tag(this LogState builder, IEnumerable<string> tags)
    {
        var tagList = new List<string>();
        if (builder.ContainsProperty("Tags") && builder["Tags"] is List<string>)
            tagList = builder["Tags"] as List<string>;

        foreach (string tag in tags)
        {
            if (!tagList.Any(s => s.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                tagList.Add(tag);
        }

        return builder.Property("Tags", tagList);
    }

    public static LogState Properties(this LogState builder, ICollection<KeyValuePair<string, string>> collection)
    {
        if (collection == null)
            return builder;

        foreach (var pair in collection)
            if (pair.Key != null)
                builder.Property(pair.Key, pair.Value);

        return builder;
    }
}
