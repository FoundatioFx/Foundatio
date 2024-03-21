using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Foundatio;

internal static class FoundatioDiagnostics
{
    internal static readonly AssemblyName AssemblyName = typeof(FoundatioDiagnostics).Assembly.GetName();
    internal static readonly string AssemblyVersion = typeof(FoundatioDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? AssemblyName.Version.ToString();
    internal static readonly ActivitySource ActivitySource = new(AssemblyName.Name, AssemblyVersion);
    internal static readonly Meter Meter = new("Foundatio", AssemblyVersion);
}
