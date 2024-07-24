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
}
