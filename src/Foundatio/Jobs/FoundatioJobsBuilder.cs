using System;
using Foundatio.Extensions;
using Foundatio.Jobs.Cronos;
using Foundatio.Jobs.Store;
using Foundatio.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foundatio.Jobs;

/// <summary>Configures Foundatio jobs. Every call to <c>AddFoundatioJobs</c> adds to the same configuration.</summary>
public sealed class FoundatioJobsBuilder
{
    private readonly JobsRegistrations _registrations;

    internal FoundatioJobsBuilder(IServiceCollection services, JobsRegistrations registrations)
    {
        Services = services;
        _registrations = registrations;
    }

    /// <summary>The service collection being configured, for provider extensions that register their own services.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Stores jobs in memory. They are lost when the process stops, and are not shared between processes.</summary>
    public FoundatioJobsBuilder UseInMemory()
    {
        return UseStore(sp => new InMemoryJobStore(JobWorkerOptions.ResolveTimeProvider(sp)));
    }

    /// <summary>Uses a store you create and dispose yourself, for example one shared by several hosts in a test.</summary>
    /// <remarks>Ignored when <c>AddFoundatioTestHarness()</c> is registered, before or after this call: the harness owns the store.</remarks>
    public FoundatioJobsBuilder UseStore(IJobStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (TestHarnessRegistration.IsRegistered(Services))
            return this;

        Services.RemoveAll<IJobStore>();
        Services.AddSingleton(store);
        return this;
    }

    /// <summary>Uses a store created, and disposed, by the service provider.</summary>
    /// <remarks>Ignored when <c>AddFoundatioTestHarness()</c> is registered, before or after this call: the harness owns the store.</remarks>
    public FoundatioJobsBuilder UseStore(Func<IServiceProvider, IJobStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (TestHarnessRegistration.IsRegistered(Services))
            return this;

        Services.ReplaceSingleton(factory);
        return this;
    }

    /// <summary>
    /// Changes <see cref="JobWorkerOptions"/>. The same as <c>services.Configure&lt;JobWorkerOptions&gt;(configure)</c>:
    /// callbacks from both, and options bound from configuration, run in the order they were added when jobs are
    /// first resolved.
    /// </summary>
    public FoundatioJobsBuilder Configure(Action<JobWorkerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Runs <typeparamref name="TJob"/> in this process when it is enqueued. It implements <see cref="IJobHandler{TArgs}"/>
    /// (enqueued by its arguments), <see cref="IJobHandler"/> (enqueued by its type), or both.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TJob"/> implements neither. Register a legacy <see cref="IJob"/> with <see cref="AddLegacyJob{TJob}"/>,
    /// or run it continuously with <c>services.AddJob&lt;TJob&gt;()</c> from Foundatio.Extensions.Hosting.
    /// </exception>
    /// <remarks>
    /// Registering a job again is allowed: options given later replace the defaults of a registration without options,
    /// such as the one <see cref="AddCronJob{TJob}(string, CronJobOptions?)"/> adds, but two different sets of options
    /// for one job throw.
    /// </remarks>
    public FoundatioJobsBuilder AddJob<TJob>(JobTypeOptions? options = null) where TJob : class
    {
        bool hasOptions = options is not null;
        options ??= new JobTypeOptions();
        options.Validate();

        foreach (var registration in JobRegistration.For(typeof(TJob), options, hasOptions))
        {
            if (_registrations.Jobs.TryGetValue(registration.Name, out var existing))
            {
                if (existing.JobType != registration.JobType)
                    throw new ArgumentException($"Job name \"{registration.Name}\" is used by both {JobNaming.GetDisplayName(existing.JobType)} and {JobNaming.GetDisplayName(registration.JobType)}. Give one a unique name with [Job(Name = \"...\")].");

                if (!hasOptions || existing.Options == options)
                    continue;

                if (existing.HasOptions)
                {
                    throw new ArgumentException($"{JobNaming.GetDisplayName(registration.JobType)} is already registered with different JobTypeOptions ({JobRegistration.Describe(existing.Options)}, "
                        + $"then {JobRegistration.Describe(options)}). Set its options once, on AddJob or on AddCronJob's CronJobOptions.JobTypeOptions.");
                }
            }

            _registrations.Jobs[registration.Name] = registration;
        }

        return this;
    }

    /// <summary>
    /// Runs a legacy <see cref="IJob"/>, such as a <see cref="JobBase"/>, when it is enqueued, through
    /// <see cref="LegacyJob{TJob}"/>. Enqueue it with <c>EnqueueAsync&lt;LegacyJob&lt;TJob&gt;&gt;()</c> and schedule it with
    /// <c>AddCronJob&lt;LegacyJob&lt;TJob&gt;&gt;(...)</c>. It keeps <typeparamref name="TJob"/>'s job name.
    /// </summary>
    public FoundatioJobsBuilder AddLegacyJob<TJob>(JobTypeOptions? options = null) where TJob : class, IJob
    {
        return AddJob<LegacyJob<TJob>>(options);
    }

    /// <summary>
    /// Runs <typeparamref name="TJob"/> on a CRON schedule, once per occurrence across all instances, and registers it
    /// to run in this process. Schedule a legacy <see cref="IJob"/> as <c>AddCronJob&lt;LegacyJob&lt;TJob&gt;&gt;(...)</c>.
    /// </summary>
    /// <param name="cronExpression">Five fields (minute precision) or six (seconds first), for example <c>"0 * * * *"</c> for hourly.</param>
    /// <param name="options">The schedule's time zone, name, overlap and misfire behavior.</param>
    public FoundatioJobsBuilder AddCronJob<TJob>(string cronExpression, CronJobOptions? options = null) where TJob : class, IJobHandler
    {
        options ??= new CronJobOptions();
        AddJob<TJob>(options.JobTypeOptions);
        return AddSchedule(JobNaming.GetName(typeof(TJob)), cronExpression, null, options);
    }

    /// <summary>
    /// Enqueues the job that takes <paramref name="arguments"/> on a CRON schedule, once per occurrence across all
    /// instances. Register the job itself with <see cref="AddJob{TJob}"/> in the process that should run it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> sets <see cref="CronJobOptions.JobTypeOptions"/>. This overload does not register the
    /// job, so set its options with <see cref="AddJob{TJob}"/> in the process that runs it.
    /// </exception>
    public FoundatioJobsBuilder AddCronJob<TArgs>(string cronExpression, TArgs arguments, CronJobOptions? options = null) where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(arguments);
        JobRegistration.ThrowIfJob(arguments, job => $"AddCronJob<{job}>(cronExpression)", nameof(arguments));
        if (options?.JobTypeOptions is not null)
        {
            throw new ArgumentException($"CronJobOptions.JobTypeOptions has no effect on a schedule of {arguments.GetType().Name} arguments, which does not register the job that runs them. "
                + "Set the options with AddJob<TJob>(options) in the process that runs it.", nameof(options));
        }

        return AddSchedule(JobNaming.GetName(arguments.GetType()), cronExpression, arguments, options ?? new CronJobOptions());
    }

    private FoundatioJobsBuilder AddSchedule(string jobName, string cronExpression, object? arguments, CronJobOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MisfireThreshold, TimeSpan.Zero, nameof(options.MisfireThreshold));
        options.JobTypeOptions?.Validate();

        string name = options.Name is { } scheduleName ? Messaging.MessageTypeRegistry.ValidateName(scheduleName, nameof(options.Name)) : jobName;
        CronExpression expression;
        try
        {
            string trimmed = cronExpression.Trim();
            expression = CronExpression.Parse(trimmed, trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"\"{cronExpression}\" for schedule {name} is not a valid CRON expression: {ex.Message}", nameof(cronExpression), ex);
        }

        if (_registrations.Schedules.ContainsKey(name))
            throw new ArgumentException($"A schedule named \"{name}\" is already registered. Give it a unique CronJobOptions.Name.", nameof(options));

        _registrations.Schedules.Add(name, new CronSchedule(name, jobName, expression, arguments, options));
        return this;
    }
}
