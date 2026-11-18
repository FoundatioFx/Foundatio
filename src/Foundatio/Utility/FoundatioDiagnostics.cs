using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Foundatio;

public static class FoundatioDiagnostics
{
    internal static readonly AssemblyName AssemblyName = typeof(FoundatioDiagnostics).Assembly.GetName();
    internal static readonly string AssemblyVersion = typeof(FoundatioDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? AssemblyName.Version?.ToString() ?? "0.0.0";
    public static readonly ActivitySource ActivitySource = new(AssemblyName.Name ?? "Foundatio", AssemblyVersion);
    public static readonly Meter Meter = new("Foundatio", AssemblyVersion);

    /// <summary>
    /// Sets the activity status to Error and records the exception details.
    /// </summary>
    /// <param name="activity">The activity to set error status on.</param>
    /// <param name="exception">The exception that caused the error (optional).</param>
    /// <param name="message">A custom error message (optional, defaults to exception message).</param>
    public static void SetErrorStatus(this Activity activity, Exception? exception = null, string? message = null)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, message ?? exception?.Message);

        if (exception is not null)
            activity.AddException(exception);
    }
}
