using System;
using System.Reflection;
using Foundatio.Messaging;

namespace Foundatio.Jobs;

internal static class JobNaming
{
    /// <summary>
    /// The stable name of a job: the <see cref="JobAttribute.Name"/> declared on the type itself, or the type's full name.
    /// A derived type does not inherit its base type's name, so it is a different job.
    /// </summary>
    public static string GetName(Type type)
    {
        if (type.GetCustomAttribute<JobAttribute>(inherit: false)?.Name is { Length: > 0 } name)
            return MessageTypeRegistry.ValidateName(name, nameof(JobAttribute.Name));

        return MessageTypeRegistry.NormalizeName((type.FullName ?? type.Name).Replace('+', '.'));
    }
}
