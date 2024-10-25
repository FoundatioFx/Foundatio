using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

public interface IJobManager
{
    void AddOrUpdate<TJob>(string cronSchedule, Action<ScheduledJobOptionsBuilder> configure = null) where TJob : class, IJob;
    void AddOrUpdate(string jobName, string cronSchedule, Action<ScheduledJobOptionsBuilder> configure = null);
    void AddOrUpdate(string jobName, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action, Action<ScheduledJobOptionsBuilder> configure = null);
    void AddOrUpdate(string jobName, string cronSchedule, Func<CancellationToken, Task> action, Action<ScheduledJobOptionsBuilder> configure = null);
    void AddOrUpdate(string jobName, string cronSchedule, Func<Task> action, Action<ScheduledJobOptionsBuilder> configure = null);
    void AddOrUpdate(string jobName, string cronSchedule, Action<IServiceProvider, CancellationToken> action, Action<ScheduledJobOptionsBuilder> configure = null);
    void AddOrUpdate(string jobName, string cronSchedule, Action<CancellationToken> action, Action<ScheduledJobOptionsBuilder> configure = null);
    void AddOrUpdate(string jobName, string cronSchedule, Action action, Action<ScheduledJobOptionsBuilder> configure = null);
    void Remove<TJob>() where TJob : class, IJob;
    void Remove(string jobName);
    JobStatus[] GetJobStatus();
    Task RunJobAsync<TJob>(CancellationToken cancellationToken = default) where TJob : class, IJob;
    Task RunJobAsync(string jobName, CancellationToken cancellationToken = default);
}

public class JobManager : IJobManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICacheClient _cacheClient;
    private readonly List<ScheduledJobRunner> _jobs = new();
    private ScheduledJobRunner[] _jobsArray;
    private readonly object _lock = new();

    public JobManager(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        var cacheClient = serviceProvider.GetService<ICacheClient>();
        bool hasCacheClient = cacheClient != null;
        _cacheClient = cacheClient ?? new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory));
        _jobs.AddRange(serviceProvider.GetServices<ScheduledJobRegistration>().Select(j => new ScheduledJobRunner(j.Options, serviceProvider, _cacheClient, loggerFactory)));
        _jobsArray = _jobs.ToArray();
        if (_jobs.Any(j => j.Options.IsDistributed && !hasCacheClient))
            throw new ArgumentException("A distributed cache client is required to run distributed jobs.");
    }

    public void AddOrUpdate<TJob>(string cronSchedule, Action<ScheduledJobOptionsBuilder> configure = null) where TJob : class, IJob
    {
        string jobName = typeof(TJob).FullName;
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name == jobName);
            if (job == null)
            {
                var options = new ScheduledJobOptions
                {
                    CronSchedule = cronSchedule,
                    Name = jobName,
                    JobFactory = sp => sp.GetRequiredService<TJob>()
                };
                var builder = new ScheduledJobOptionsBuilder(options);
                configure?.Invoke(builder);
                _jobs.Add(new ScheduledJobRunner(options, _serviceProvider, _cacheClient, _loggerFactory));
                _jobsArray = _jobs.ToArray();
            }
            else
            {
                var builder = new ScheduledJobOptionsBuilder(job.Options);
                builder.CronSchedule(cronSchedule);
                configure?.Invoke(builder);
                job.Schedule = job.Options.CronSchedule;
            }
        }
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name == jobName);
            if (job == null)
            {
                var options = new ScheduledJobOptions
                {
                    CronSchedule = cronSchedule,
                    Name = jobName
                };
                var builder = new ScheduledJobOptionsBuilder(options);
                configure?.Invoke(builder);
                options.JobFactory = options.JobFactory;
                _jobs.Add(new ScheduledJobRunner(options, _serviceProvider, _cacheClient, _loggerFactory));
                _jobsArray = _jobs.ToArray();
            }
            else
            {
                var builder = new ScheduledJobOptionsBuilder(job.Options);
                builder.CronSchedule(cronSchedule);
                configure?.Invoke(builder);
                job.Schedule = job.Options.CronSchedule;
            }
        }
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        AddOrUpdate(jobName, cronSchedule, b => b.JobFactory(sp => new DynamicJob(sp, action)));
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Func<CancellationToken, Task> action, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        AddOrUpdate(jobName, cronSchedule, (_, ct) => action(ct), configure);
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Func<Task> action, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        AddOrUpdate(jobName, cronSchedule, (_, _) => action(), configure);
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action<IServiceProvider, CancellationToken> action, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        AddOrUpdate(jobName, cronSchedule, (sp, ct) =>
        {
            action(sp, ct);
            return Task.CompletedTask;
        }, configure);
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action<CancellationToken> action, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        AddOrUpdate(jobName, cronSchedule, (_, ct) =>
        {
            action(ct);
            return Task.CompletedTask;
        }, configure);
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action action, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        AddOrUpdate(jobName, cronSchedule, (_, _) =>
        {
            action();
            return Task.CompletedTask;
        }, configure);
    }

    public void Remove<TJob>() where TJob : class, IJob
    {
        string jobName = typeof(TJob).FullName;
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name == jobName);
            if (job == null)
                return;

            _jobs.Remove(job);
            _jobsArray = _jobs.ToArray();
        }
    }

    public void Remove(string jobName)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Options.Name == jobName);
            if (job == null)
                return;

            _jobs.Remove(job);
            _jobsArray = _jobs.ToArray();
        }
    }

    public JobStatus[] GetJobStatus()
    {
        return Jobs.Select(j => new JobStatus
        {
            Name = j.Options.Name,
            Schedule = j.Options.CronSchedule,
            LastRun = j.LastRun,
            LastSuccess = j.LastSuccess,
            NextRun = j.NextRun,
            IsRunning = j.RunTask is { IsCompleted: false }
        }).ToArray();
    }

    public async Task RunJobAsync<TJob>(CancellationToken cancellationToken = default) where TJob : class, IJob
    {
        string jobName = typeof(TJob).FullName;
        await RunJobAsync(jobName, cancellationToken).AnyContext();
    }

    public async Task RunJobAsync(string jobName, CancellationToken cancellationToken = default)
    {
        var job = Jobs.FirstOrDefault(j => j.Options.Name == jobName);
        if (job == null)
            throw new ArgumentException("Job not found.", nameof(jobName));

        using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job: " + job.Options.Name, ActivityKind.Server);
        await job.StartAsync(cancellationToken).AnyContext();
    }

    internal ScheduledJobRunner[] Jobs => _jobsArray;
}

public class JobStatus
{
    public string Name { get; set; }
    public string Schedule { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? LastSuccess { get; set; }
    public string LastErrorMessage { get; set; }
    public DateTime? NextRun { get; set; }
    public bool IsRunning { get; set; }
}
