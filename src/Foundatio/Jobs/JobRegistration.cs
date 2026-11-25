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
    /// <summary>The options runs use: <see cref="ConfiguredOptions"/>, with the test harness's retry delay if it sets one.</summary>
    public JobTypeOptions Options { get; private set; } = new();

    /// <summary>The options as registered, or as <see cref="OptionsFactory"/> returned them.</summary>
    public JobTypeOptions ConfiguredOptions { get; private set; } = new();

    /// <summary>Creates the options from the app's services when the runtime is built, for options registered with a factory.</summary>
    public Func<IServiceProvider, JobTypeOptions>? OptionsFactory { get; private init; }

    /// <summary>Whether options were given when the job was registered, rather than defaulted.</summary>
    public bool HasOptions { get; private init; }

    /// <summary>
    /// Calls <see cref="OptionsFactory"/> and validates what it returns, and applies <paramref name="retryDelay"/> to the
    /// job type's own retry policy. Runs when the runtime is built.
    /// </summary>
    public void Resolve(IServiceProvider services, TimeSpan? retryDelay)
    {
        if (OptionsFactory is not null)
        {
            var options = OptionsFactory(services) ?? throw new InvalidOperationException($"The options factory of job {Name} returned null.");
            options.Validate();
            ConfiguredOptions = options;
        }

        Options = retryDelay is { } delay && ConfiguredOptions.RetryPolicy is { } policy
            ? ConfiguredOptions with { RetryPolicy = policy with { Delay = Resilience.RetryPolicy.Fixed(delay) } }
            : ConfiguredOptions;
    }

    /// <summary>Whether this registration and <paramref name="other"/> set the same options the same way.</summary>
    public bool HasSameOptions(JobRegistration other) => OptionsFactory == other.OptionsFactory && ConfiguredOptions == other.ConfiguredOptions;

    /// <summary>Runs the job with its deserialized arguments (null for a job without arguments). Throws when the run fails.</summary>
    public required Func<IServiceProvider, object?, IJobContext, CancellationToken, Task> Invoke { get; init; }

    public static IReadOnlyList<JobRegistration> For(Type jobType, JobTypeOptions options, bool hasOptions = false, Func<IServiceProvider, JobTypeOptions>? optionsFactory = null)
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
                ConfiguredOptions = options,
                OptionsFactory = optionsFactory,
                HasOptions = hasOptions,
                Invoke = (Func<IServiceProvider, object?, IJobContext, CancellationToken, Task>)typeof(JobRegistration)
                    .GetMethod(nameof(CreateInvoker), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(jobType, argumentsType)
                    .Invoke(null, null)!
            })
            .ToList();

        // A job with arguments is named after its arguments type, so producers can enqueue it without the job class.
        bool argumentFree = typeof(IJobHandler).IsAssignableFrom(jobType);
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
                ConfiguredOptions = options,
                OptionsFactory = optionsFactory,
                HasOptions = hasOptions,
                Invoke = (services, _, context, cancellationToken) =>
                    ((IJobHandler)ActivatorUtilities.GetServiceOrCreateInstance(services, jobType)).HandleAsync(context, cancellationToken)
            });
        }

        if (registrations.Count == 0)
        {
            string name = jobType.Name;
            throw new ArgumentException(typeof(IJob).IsAssignableFrom(jobType)
                ? $"{name} is a legacy IJob, which durable jobs do not run directly. To run it when it is enqueued or on a CRON schedule, register it with "
                    + $"AddLegacyJob<{name}>() (schedule it with AddCronJob<LegacyJob<{name}>>(...)), or implement IJobHandler. To run it continuously as a hosted "
                    + $"service, register it with services.AddJob<{name}>() from Foundatio.Extensions.Hosting instead."
                : $"{jobType.FullName} does not implement IJobHandler or IJobHandler<TArgs>.", nameof(jobType));
        }

        return registrations;
    }

    /// <summary>Describes job type options for an error message.</summary>
    public static string Describe(JobTypeOptions options)
    {
        string concurrency = options.MaxConcurrency is { } max ? $"MaxConcurrency {max}" : "MaxConcurrency not set";
        string retries = options.RetryPolicy is { } policy ? $"RetryPolicy with MaxAttempts {policy.MaxAttempts}" : "RetryPolicy not set";
        string retention = options.RetainSucceededFor is { } retain ? $", RetainSucceededFor {retain}" : "";
        return $"{concurrency}, {retries}{retention}";
    }

    /// <summary>The arguments types of the <see cref="IJobHandler{TArgs}"/> interfaces <paramref name="jobType"/> implements.</summary>
    public static IEnumerable<Type> GetArgumentsTypes(Type jobType)
    {
        return jobType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJobHandler<>))
            .Select(i => i.GetGenericArguments()[0]);
    }

    /// <summary>Throws when a job was passed where its arguments belong, naming the call to make instead.</summary>
    /// <param name="arguments">What was passed as job arguments.</param>
    /// <param name="argumentFreeCall">Formats the call that takes a job type, given the type's name.</param>
    /// <param name="paramName">The parameter <paramref name="arguments"/> was passed as.</param>
    public static void ThrowIfJob(object arguments, Func<string, string> argumentFreeCall, string paramName)
    {
        var type = arguments.GetType();
        if (arguments is EnqueueOptions or JobTypeOptions or CronJobOptions)
        {
            throw new ArgumentException($"{type.Name} is options, not job arguments. For a job without arguments, call {argumentFreeCall("TJob")} with the job class as TJob, "
                + "passing the options; for a job with arguments, pass the arguments first.", paramName);
        }

        if (arguments is IJobHandler)
            throw new ArgumentException($"{type.Name} is a job, not job arguments. Call {argumentFreeCall(type.Name)} instead.", paramName);

        if (arguments is IJob)
            throw new ArgumentException($"{type.Name} is a legacy job, not job arguments. Call {argumentFreeCall($"LegacyJob<{type.Name}>")} instead, and register it with AddLegacyJob<{type.Name}>().", paramName);

        if (GetArgumentsTypes(type).Any())
            throw new ArgumentException($"{type.Name} is a job, not job arguments. Pass the arguments it takes instead.", paramName);
    }

    private static Func<IServiceProvider, object?, IJobContext, CancellationToken, Task> CreateInvoker<TJob, TArgs>()
        where TJob : class, IJobHandler<TArgs> where TArgs : class
    {
        return (services, arguments, context, cancellationToken) =>
            ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services).HandleAsync((TArgs)arguments!, context, cancellationToken);
    }
}
