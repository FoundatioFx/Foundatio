#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Foundatio;

/// <summary>
/// Finds where an assembly's public, non-experimental API hands out the experimental provider SPI, so app code could
/// use it without seeing FOUNDATIO0001.
/// </summary>
internal static class ExperimentalApiBoundary
{
    /// <summary>
    /// Lists every leak: a public member of a non-experimental type whose return, property or field type is experimental,
    /// a non-experimental type deriving from an experimental class, and an experimental interface member implemented by a
    /// public method instead of explicitly. Taking an SPI type as a parameter is allowed (that is how
    /// <c>UseTransport</c>/<c>UseStore</c> register one), and so is <c>IMessageTransport.Capabilities</c>, whose type is
    /// public API.
    /// </summary>
    public static IReadOnlyList<string> FindLeaks(Assembly assembly)
    {
        var leaks = new List<string>();
        foreach (var type in assembly.GetExportedTypes().Where(t => !IsExperimental(t)))
        {
            if (type.BaseType is { } baseType && References(baseType))
                leaks.Add($"{type.FullName} derives from {baseType.FullName}");

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(Flags).Where(m => !IsExperimental(m)))
            {
                var exposed = member switch
                {
                    MethodInfo { IsSpecialName: false } method => method.ReturnType,
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    _ => null
                };

                if (exposed is not null && References(exposed))
                    leaks.Add($"{type.FullName}.{member.Name} exposes {exposed}");
            }

            if (type.IsInterface)
                continue;

            foreach (var spi in type.GetInterfaces().Where(IsExperimental))
            {
                var map = type.GetInterfaceMap(spi);
                for (int i = 0; i < map.TargetMethods.Length; i++)
                {
                    var target = map.TargetMethods[i];
                    if (target.IsPublic && target.DeclaringType == type && !IsExperimental(target) && map.InterfaceMethods[i].Name != "get_Capabilities")
                        leaks.Add($"{type.FullName}.{target.Name} implements {spi.Name}.{map.InterfaceMethods[i].Name} publicly");
                }
            }
        }

        return leaks;
    }

    private static bool References(Type type)
    {
        if (type.HasElementType)
            return References(type.GetElementType()!);
        if (type.IsGenericParameter)
            return false;
        if (IsExperimental(type))
            return true;

        return type.IsGenericType && type.GetGenericArguments().Any(References);
    }

    private static bool IsExperimental(MemberInfo member)
    {
        for (var current = member; current is not null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(ExperimentalAttribute), inherit: false))
                return true;
        }

        return false;
    }
}
