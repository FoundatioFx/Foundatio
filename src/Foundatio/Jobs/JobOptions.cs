using System;
using System.Reflection;
using Foundatio.Utility;

namespace Foundatio.Jobs;

public class JobOptions
{
    public string Name { get; set; }
    public string Description { get; set; }
    public Func<IServiceProvider, IJob> JobFactory { get; set; }
    public bool RunContinuous { get; set; } = true;
    public TimeSpan? Interval { get; set; }
    public TimeSpan? InitialDelay { get; set; }
    public int IterationLimit { get; set; } = -1;
    public int InstanceCount { get; set; } = 1;

    public static JobOptions GetDefaults(Type jobType)
    {
        var jobOptions = new JobOptions();
        ApplyDefaults(jobOptions, jobType);
        return jobOptions;
    }

    public static void ApplyDefaults(JobOptions jobOptions, Type jobType)
    {
        var jobAttribute = jobType.GetCustomAttribute<JobAttribute>() ?? new JobAttribute();

        jobOptions.Name = jobAttribute.Name;
        if (String.IsNullOrEmpty(jobOptions.Name))
        {
            string jobName = jobType.Name;
            if (jobName.EndsWith("Job"))
                jobName = jobName.Substring(0, jobName.Length - 3);

            jobOptions.Name = jobName.ToLower();
        }

        jobOptions.Description = jobAttribute.Description;
        jobOptions.RunContinuous = jobAttribute.IsContinuous;

        if (!String.IsNullOrEmpty(jobAttribute.Interval))
        {
            TimeSpan? interval;
            if (TimeUnit.TryParse(jobAttribute.Interval, out interval))
                jobOptions.Interval = interval;
        }

        if (!String.IsNullOrEmpty(jobAttribute.InitialDelay))
        {
            TimeSpan? delay;
            if (TimeUnit.TryParse(jobAttribute.InitialDelay, out delay))
                jobOptions.InitialDelay = delay;
        }

        jobOptions.IterationLimit = jobAttribute.IterationLimit;
        jobOptions.InstanceCount = jobAttribute.InstanceCount;
    }

    public static JobOptions GetDefaults<T>() where T : IJob
    {
        return GetDefaults(typeof(T));
    }

    public static JobOptions GetDefaults(IJob instance)
    {
        var jobOptions = GetDefaults(instance.GetType());
        jobOptions.JobFactory = _ => instance;
        return jobOptions;
    }

    public static JobOptions GetDefaults<T>(IJob instance) where T : IJob
    {
        var jobOptions = GetDefaults<T>();
        jobOptions.JobFactory = _ => instance;
        return jobOptions;
    }

    public static JobOptions GetDefaults(Type jobType, Func<IServiceProvider, IJob> jobFactory)
    {
        var jobOptions = GetDefaults(jobType);
        jobOptions.JobFactory = jobFactory;
        return jobOptions;
    }

    public static JobOptions GetDefaults<T>(Func<IServiceProvider, IJob> jobFactory) where T : IJob
    {
        var jobOptions = GetDefaults<T>();
        jobOptions.JobFactory = jobFactory;
        return jobOptions;
    }
}

public static class JobOptionExtensions
{
    public static void ApplyDefaults<T>(this JobOptions jobOptions)
    {
        JobOptions.ApplyDefaults(jobOptions, typeof(T));
    }
}
