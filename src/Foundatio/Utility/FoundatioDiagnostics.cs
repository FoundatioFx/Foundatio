using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Foundatio;

public static class FoundatioDiagnostics
{
    internal static readonly AssemblyName AssemblyName = typeof(FoundatioDiagnostics).Assembly.GetName();
    internal static readonly string AssemblyVersion = typeof(FoundatioDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? AssemblyName.Version.ToString();
    public static readonly ActivitySource ActivitySource = new(AssemblyName.Name, AssemblyVersion);
    public static readonly Meter Meter = new("Foundatio", AssemblyVersion);

    /// <summary>
    /// Sets the activity status to Error and records the exception details.
    /// </summary>
    /// <param name="activity">The activity to set error status on.</param>
    /// <param name="exception">The exception that caused the error (optional).</param>
    /// <param name="message">A custom error message (optional, defaults to exception message).</param>
    public static void SetErrorStatus(this Activity activity, Exception exception = null, string message = null)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, message ?? exception?.Message);

        if (exception is not null)
            activity.AddException(exception);
    }
}

#if !NET10_0_OR_GREATER
internal static class ActivityAddExceptionPolyfill
{
    private const string ExceptionEventName = "exception";
    private const string ExceptionMessageTag = "exception.message";
    private const string ExceptionStackTraceTag = "exception.stacktrace";
    private const string ExceptionTypeTag = "exception.type";

    /// <summary>
    /// Polyfill for Activity.AddException available in .NET 10+.
    /// Add an <see cref="ActivityEvent" /> containing exception information to the <see cref="Activity.Events" /> list.
    /// </summary>
    /// <param name="activity">The activity to record the exception on.</param>
    /// <param name="exception">The exception to add to the attached events list.</param>
    /// <param name="tags">The tags to add to the exception event.</param>
    /// <param name="timestamp">The timestamp to add to the exception event.</param>
    /// <returns><see langword="this" /> for convenient chaining.</returns>
    public static Activity AddException(this Activity activity, Exception exception, in TagList tags = default, DateTimeOffset timestamp = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(exception);

        var exceptionTags = new ActivityTagsCollection();

        for (int i = 0; i < tags.Count; i++)
            exceptionTags.Add(tags[i]);

        if (!exceptionTags.ContainsKey(ExceptionMessageTag))
            exceptionTags.Add(ExceptionMessageTag, exception.Message);

        if (!exceptionTags.ContainsKey(ExceptionStackTraceTag))
            exceptionTags.Add(ExceptionStackTraceTag, exception.ToString());

        if (!exceptionTags.ContainsKey(ExceptionTypeTag))
            exceptionTags.Add(ExceptionTypeTag, exception.GetType().ToString());

        return activity.AddEvent(new ActivityEvent(ExceptionEventName, timestamp, exceptionTags));
    }
}
#endif