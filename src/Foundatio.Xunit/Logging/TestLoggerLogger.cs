﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Xunit;

internal class TestLoggerLogger : ILogger
{
    private readonly TestLogger _logger;
    private readonly string _categoryName;

    public TestLoggerLogger(string categoryName, TestLogger logger)
    {
        _logger = logger;
        _categoryName = categoryName;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!_logger.IsEnabled(_categoryName, logLevel))
            return;

        object[] scopes = _logger.Options.IncludeScopes ? CurrentScopeStack.Reverse().ToArray() : Array.Empty<object>();
        var logEntry = new LogEntry
        {
            Date = _logger.Options.GetNow(),
            LogLevel = logLevel,
            EventId = eventId,
            State = state,
            Exception = exception,
            Formatter = (s, e) => formatter((TState)s, e),
            CategoryName = _categoryName,
            Scopes = scopes
        };

        switch (state)
        {
            //case LogData logData:
            //    logEntry.Properties["CallerMemberName"] = logData.MemberName;
            //    logEntry.Properties["CallerFilePath"] = logData.FilePath;
            //    logEntry.Properties["CallerLineNumber"] = logData.LineNumber;

            //    foreach (var property in logData.Properties)
            //        logEntry.Properties[property.Key] = property.Value;
            //    break;
            case IDictionary<string, object> logDictionary:
                foreach (var property in logDictionary)
                    logEntry.Properties[property.Key] = property.Value;
                break;
        }

        foreach (object scope in scopes)
        {
            if (!(scope is IDictionary<string, object> scopeData))
                continue;

            foreach (var property in scopeData)
                logEntry.Properties[property.Key] = property.Value;
        }

        _logger.AddLogEntry(logEntry);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _logger.IsEnabled(_categoryName, logLevel);
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        return Push(state);
    }

    public IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        return Push(scopeFactory(state));
    }

    private static readonly AsyncLocal<Wrapper> _currentScopeStack = new();

    private sealed class Wrapper
    {
        public ImmutableStack<object> Value { get; set; }
    }

    private static ImmutableStack<object> CurrentScopeStack
    {
        get => _currentScopeStack.Value?.Value ?? ImmutableStack.Create<object>();
        set => _currentScopeStack.Value = new Wrapper { Value = value };
    }

    private static IDisposable Push(object state)
    {
        CurrentScopeStack = CurrentScopeStack.Push(state);
        return new DisposableAction(Pop);
    }

    private static void Pop()
    {
        CurrentScopeStack = CurrentScopeStack.Pop();
    }
}
