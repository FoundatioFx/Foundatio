using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Jobs;

internal sealed class JobRegistration
{
    public required string Name { get; init; }
    public required Type JobType { get; init; }
    public Type? ArgumentsType { get; init; }
    public required JobTypeOptions Options { get; init; }

    /// <summary>Runs the job. Returns null on success, or the legacy <see cref="JobResult"/> of an <see cref="IJob"/>.</summary>
    public required Func<IServiceProvider, object?, IJobContext, CancellationToken, Task<JobResult?>> Invoke { get; init; }

    public static IReadOnlyList<JobRegistration> For(Type jobType, JobTypeOptions options)
    {
        if (jobType.IsAbstract || jobType.IsInterface)
            throw new ArgumentException($"{jobType.FullName} must be a concrete class.", nameof(jobType));

        var registrations = jobType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJob<>))
            .Select(i => i.GetGenericArguments()[0])
            .Select(argumentsType => new JobRegistration
            {
                Name = JobNaming.GetName(argumentsType),
                JobType = jobType,
                ArgumentsType = argumentsType,
                Options = options,
                Invoke = (Func<IServiceProvider, object?, IJobContext, CancellationToken, Task<JobResult?>>)typeof(JobRegistration)
                    .GetMethod(nameof(CreateInvoker), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(jobType, argumentsType)
                    .Invoke(null, null)!
            })
            .ToList();

        // A job with arguments is named after its arguments type, so producers can enqueue it without the job class.
        if (registrations.Count > 0 && !typeof(IJob).IsAssignableFrom(jobType) && jobType.GetCustomAttribute<JobAttribute>()?.Name is { Length: > 0 } ignored)
        {
            throw new ArgumentException($"[Job(Name = \"{ignored}\")] on {jobType.FullName} has no effect: a job with arguments is named after its arguments type. "
                + $"Put the attribute on {String.Join(" and ", registrations.Select(r => r.ArgumentsType!.Name))} instead.", nameof(jobType));
        }

        if (typeof(IJob).IsAssignableFrom(jobType))
        {
            registrations.Add(new JobRegistration
            {
                Name = JobNaming.GetName(jobType),
                JobType = jobType,
                Options = options,
                Invoke = async (services, _, _, cancellationToken) =>
                    await ((IJob)ActivatorUtilities.GetServiceOrCreateInstance(services, jobType)).RunAsync(cancellationToken).AnyContext()
            });
        }

        if (registrations.Count == 0)
            throw new ArgumentException($"{jobType.FullName} does not implement IJob or IJob<TArgs>.", nameof(jobType));

        return registrations;
    }

    private static Func<IServiceProvider, object?, IJobContext, CancellationToken, Task<JobResult?>> CreateInvoker<TJob, TArgs>()
        where TJob : class, IJob<TArgs> where TArgs : class
    {
        return async (services, arguments, context, cancellationToken) =>
        {
            await ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services).RunAsync((TArgs)arguments!, context, cancellationToken).AnyContext();
            return null;
        };
    }
}
