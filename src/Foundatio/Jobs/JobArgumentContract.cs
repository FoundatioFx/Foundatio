using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Foundatio.Jobs;

internal static class JobArgumentContract
{
    private static readonly ConcurrentDictionary<Type, Type[]> _contracts = new();

    public static void ValidateType(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        if (!typeof(IJob).IsAssignableFrom(jobType) || jobType.IsAbstract || jobType.IsInterface || jobType.ContainsGenericParameters)
            throw new ArgumentException($"Job {jobType.Name} must be a concrete type implementing IJob.", nameof(jobType));
    }

    public static void Validate(Type jobType, object? arguments)
    {
        ValidateType(jobType);
        var types = _contracts.GetOrAdd(jobType, static type => type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJob<>))
            .Select(i => i.GenericTypeArguments[0]).ToArray());
        if (types.Length > 1)
            throw new ArgumentException($"Job {jobType.Name} must declare only one argument contract.", nameof(jobType));

        if (types.Length == 0)
        {
            if (arguments is not null)
                throw new ArgumentException($"Job {jobType.Name} does not declare an IJob<TArgs> argument contract.", nameof(arguments));
            return;
        }

        if (arguments is null || arguments.GetType() != types[0])
            throw new ArgumentException($"Job {jobType.Name} requires arguments of type {types[0].Name}. Use EnqueueAsync<TJob, TArgs>(args).", nameof(arguments));
    }
}
