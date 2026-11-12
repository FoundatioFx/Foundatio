using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Jobs;

internal sealed class JobRegistration
{
    public required string Name { get; init; }
    public required Type JobType { get; init; }
    public Type? ArgumentsType { get; init; }
    public required JobTypeOptions Options { get; init; }

    /// <summary>Runs the job with its deserialized arguments (null for a job without arguments). Throws when the run fails.</summary>
    public required Func<IServiceProvider, object?, IJobContext, CancellationToken, Task> Invoke { get; init; }

    public static IReadOnlyList<JobRegistration> For(Type jobType, JobTypeOptions options)
    {
        if (jobType.IsAbstract || jobType.IsInterface)
            throw new ArgumentException($"{jobType.FullName} must be a concrete class.", nameof(jobType));

        var registrations = GetArgumentsTypes(jobType)
            .Select(argumentsType => new JobRegistration
            {
                Name = JobNaming.GetName(argumentsType),
                JobType = jobType,
                ArgumentsType = argumentsType,
                Options = options,
                Invoke = (Func<IServiceProvider, object?, IJobContext, CancellationToken, Task>)typeof(JobRegistration)
                    .GetMethod(nameof(CreateInvoker), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(jobType, argumentsType)
                    .Invoke(null, null)!
            })
            .ToList();

        // A job with arguments is named after its arguments type, so producers can enqueue it without the job class.
        bool argumentFree = typeof(IDurableJob).IsAssignableFrom(jobType);
        if (registrations.Count > 0 && !argumentFree && jobType.GetCustomAttribute<JobAttribute>(inherit: false)?.Name is { Length: > 0 } ignored)
        {
            throw new ArgumentException($"[Job(Name = \"{ignored}\")] on {jobType.FullName} has no effect: a job with arguments is named after its arguments type. "
                + $"Put the attribute on {String.Join(" and ", registrations.Select(r => r.ArgumentsType!.Name))} instead.", nameof(jobType));
        }

        if (argumentFree)
        {
            registrations.Add(new JobRegistration
            {
                Name = JobNaming.GetName(jobType),
                JobType = jobType,
                Options = options,
                Invoke = (services, _, context, cancellationToken) =>
                    ((IDurableJob)ActivatorUtilities.GetServiceOrCreateInstance(services, jobType)).RunAsync(context, cancellationToken)
            });
        }

        if (registrations.Count == 0)
        {
            string name = jobType.Name;
            throw new ArgumentException(typeof(IJob).IsAssignableFrom(jobType)
                ? $"{name} is a legacy IJob, which durable jobs do not run directly. To run it when it is enqueued or on a CRON schedule, register it with "
                    + $"AddLegacyJob<{name}>() (schedule it with AddCronJob<LegacyJob<{name}>>(...)), or implement IDurableJob. To run it continuously as a hosted "
                    + $"service, register it with services.AddJob<{name}>() from Foundatio.Extensions.Hosting instead."
                : $"{jobType.FullName} does not implement IDurableJob or IDurableJob<TArgs>.", nameof(jobType));
        }

        return registrations;
    }

    /// <summary>The arguments types of the <see cref="IDurableJob{TArgs}"/> interfaces <paramref name="jobType"/> implements.</summary>
    public static IEnumerable<Type> GetArgumentsTypes(Type jobType)
    {
        return jobType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDurableJob<>))
            .Select(i => i.GetGenericArguments()[0]);
    }

    /// <summary>Throws when a job was passed where its arguments belong, naming the call to make instead.</summary>
    /// <param name="arguments">What was passed as job arguments.</param>
    /// <param name="argumentFreeCall">Formats the call that takes a job type, given the type's name.</param>
    /// <param name="paramName">The parameter <paramref name="arguments"/> was passed as.</param>
    public static void ThrowIfJob(object arguments, Func<string, string> argumentFreeCall, string paramName)
    {
        var type = arguments.GetType();
        if (arguments is IDurableJob)
            throw new ArgumentException($"{type.Name} is a job, not job arguments. Call {argumentFreeCall(type.Name)} instead.", paramName);

        if (arguments is IJob)
            throw new ArgumentException($"{type.Name} is a legacy job, not job arguments. Call {argumentFreeCall($"LegacyJob<{type.Name}>")} instead, and register it with AddLegacyJob<{type.Name}>().", paramName);

        if (GetArgumentsTypes(type).Any())
            throw new ArgumentException($"{type.Name} is a job, not job arguments. Pass the arguments it takes instead.", paramName);
    }

    private static Func<IServiceProvider, object?, IJobContext, CancellationToken, Task> CreateInvoker<TJob, TArgs>()
        where TJob : class, IDurableJob<TArgs> where TArgs : class
    {
        return (services, arguments, context, cancellationToken) =>
            ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services).RunAsync((TArgs)arguments!, context, cancellationToken);
    }
}
