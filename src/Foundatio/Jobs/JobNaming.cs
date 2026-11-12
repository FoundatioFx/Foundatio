using System;
using System.Reflection;
using Foundatio.Messaging;

namespace Foundatio.Jobs;

internal static class JobNaming
{
    /// <summary>
    /// The stable name of a job: the <see cref="JobAttribute.Name"/> declared on the type itself, or the type's full name.
    /// A derived type does not inherit its base type's name, so it is a different job. A <see cref="LegacyJob{TJob}"/>
    /// is named after the legacy job it runs.
    /// </summary>
    public static string GetName(Type type)
    {
        type = GetNamedType(type);
        if (type.GetCustomAttribute<JobAttribute>(inherit: false)?.Name is { Length: > 0 } name)
            return MessageTypeRegistry.ValidateName(name, nameof(JobAttribute.Name));

        return MessageTypeRegistry.NormalizeName((type.FullName ?? type.Name).Replace('+', '.'));
    }

    /// <summary>The type a job is named after, and whose <see cref="JobAttribute"/> applies: <c>TJob</c> for a <see cref="LegacyJob{TJob}"/>, otherwise the type itself.</summary>
    public static Type GetNamedType(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LegacyJob<>) ? type.GetGenericArguments()[0] : type;
    }

    /// <summary>The type's name as it is written in code, for error messages.</summary>
    public static string GetDisplayName(Type type)
    {
        var named = GetNamedType(type);
        return named == type ? type.Name : $"LegacyJob<{named.Name}>";
    }
}
