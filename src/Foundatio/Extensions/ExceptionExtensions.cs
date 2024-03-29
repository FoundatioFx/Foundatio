﻿using System;
using System.Linq;

namespace Foundatio.Utility;

internal static class ExceptionExtensions
{
    public static Exception GetInnermostException(this Exception exception)
    {
        if (exception == null)
            return null;

        Exception current = exception;
        while (current.InnerException != null)
            current = current.InnerException;

        return current;
    }

    public static string GetMessage(this Exception exception)
    {
        if (exception == null)
            return String.Empty;

        if (exception is AggregateException aggregateException)
            return String.Join(Environment.NewLine, aggregateException.Flatten().InnerExceptions.Where(ex => !String.IsNullOrEmpty(ex.GetInnermostException().Message)).Select(ex => ex.GetInnermostException().Message));

        return exception.GetInnermostException().Message;
    }
}
