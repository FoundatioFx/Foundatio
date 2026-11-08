using System;
using System.Reflection;
using Foundatio.Messaging;

namespace Foundatio.Jobs;

internal static class JobNaming
{
    /// <summary>The stable name of a job: <see cref="JobAttribute.Name"/>, or the type's full name.</summary>
    public static string GetName(Type type)
    {
        if (type.GetCustomAttribute<JobAttribute>()?.Name is { Length: > 0 } name)
            return MessageTypeRegistry.ValidateName(name, nameof(JobAttribute.Name));

        return MessageTypeRegistry.NormalizeName((type.FullName ?? type.Name).Replace('+', '.'));
    }
}
