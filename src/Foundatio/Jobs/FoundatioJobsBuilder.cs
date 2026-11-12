using System;
using Foundatio.Extensions;
using Foundatio.Jobs.Cronos;
using Foundatio.Jobs.Store;
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

    public IServiceCollection Services { get; }

    /// <summary>Stores jobs in memory. They are lost when the process stops, and are not shared between processes.</summary>
    public FoundatioJobsBuilder UseInMemory()
    {
        return UseStore(sp => new InMemoryJobStore(sp.GetService<TimeProvider>()));
    }

    /// <summary>Uses a store you create and dispose yourself, for example one shared by several hosts in a test.</summary>
    public FoundatioJobsBuilder UseStore(IJobStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Services.RemoveAll<IJobStore>();
        Services.AddSingleton(store);
        return this;
    }

    public FoundatioJobsBuilder UseStore(Func<IServiceProvider, IJobStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.ReplaceSingleton(factory);
        return this;
    }

    public FoundatioJobsBuilder Configure(Action<JobsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _registrations.ConfigureOptions.Add(configure);
        return this;
    }

    /// <summary>
    /// Runs <typeparamref name="TJob"/> in this process. It may implement <see cref="IJob{TArgs}"/> (enqueued by its
    /// arguments) and/or <see cref="IJob"/> (enqueued by its type).
    /// </summary>
    public FoundatioJobsBuilder AddJob<TJob>(JobTypeOptions? options = null) where TJob : class
    {
        options ??= new JobTypeOptions();
        options.Validate();

        foreach (var registration in JobRegistration.For(typeof(TJob), options))
        {
            if (_registrations.Jobs.TryGetValue(registration.Name, out var existing))
            {
                if (existing.JobType == registration.JobType)
                    continue;

                throw new ArgumentException($"Job name \"{registration.Name}\" is used by both {existing.JobType.Name} and {registration.JobType.Name}. Give one a unique name with [Job(Name = \"...\")].");
            }

            _registrations.Jobs.Add(registration.Name, registration);
        }

        return this;
    }

    /// <summary>Runs <typeparamref name="TJob"/> on a CRON schedule, once per occurrence across all instances.</summary>
    /// <param name="cronExpression">Five fields (minute precision) or six (seconds first), for example <c>"0 * * * *"</c> for hourly.</param>
    /// <param name="options">The schedule's time zone, name, overlap and misfire behavior.</param>
    public FoundatioJobsBuilder AddCronJob<TJob>(string cronExpression, CronJobOptions? options = null) where TJob : class, IJob
    {
        options ??= new CronJobOptions();
        AddJob<TJob>(options.JobOptions);
        return AddSchedule(JobNaming.GetName(typeof(TJob)), cronExpression, null, options);
    }

    /// <summary>
    /// Enqueues the job that takes <paramref name="arguments"/> on a CRON schedule, once per occurrence across all
    /// instances. Register the job itself with <see cref="AddJob{TJob}"/> in the process that should run it.
    /// </summary>
    public FoundatioJobsBuilder AddCronJob<TArgs>(string cronExpression, TArgs arguments, CronJobOptions? options = null) where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var argumentsType = arguments.GetType();
        if (arguments is IJob)
            throw new ArgumentException($"{argumentsType.Name} is a job, not job arguments. Call AddCronJob<{argumentsType.Name}>(cronExpression) instead.", nameof(arguments));

        return AddSchedule(JobNaming.GetName(argumentsType), cronExpression, arguments, options ?? new CronJobOptions());
    }

    private FoundatioJobsBuilder AddSchedule(string jobName, string cronExpression, object? arguments, CronJobOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MisfireThreshold, TimeSpan.Zero, nameof(options.MisfireThreshold));
        options.JobOptions?.Validate();

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
