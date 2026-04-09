using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Foundatio.Utility;

internal static class ExceptionExtensions
{
    [return: NotNullIfNotNull(nameof(exception))]
    public static Exception? GetInnermostException(this Exception? exception)
    {
        if (exception is null)
            return null;

        Exception current = exception;
        while (current.InnerException is not null)
            current = current.InnerException;

        return current;
    }

    public static string GetMessage(this Exception exception)
    {
        if (exception is null)
            return String.Empty;

        if (exception is AggregateException aggregateException)
            return String.Join(Environment.NewLine, aggregateException.Flatten().InnerExceptions.Where(ex => !String.IsNullOrEmpty(ex.GetInnermostException().Message)).Select(ex => ex.GetInnermostException().Message));

        return exception.GetInnermostException().Message;
    }
}
