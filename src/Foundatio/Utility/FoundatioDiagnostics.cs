using System;
using System.Diagnostics;
using System.Reflection;

namespace Foundatio {
    internal static class FoundatioDiagnostics {
        private static readonly AssemblyName AssemblyName = typeof(FoundatioDiagnostics).Assembly.GetName();
        internal static readonly ActivitySource ActivitySource = new(AssemblyName.Name, AssemblyName.Version.ToString());
    }
}