using System;
using System.Collections.Generic;
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
    void AddOrUpdate<TJob>(Action<ScheduledJobOptionsBuilder> configure = null) where TJob : class, IJob;
    void AddOrUpdate(string jobName, Action<ScheduledJobOptionsBuilder> configure = null);
    void Update<TJob>(Action<ScheduledJobOptionsBuilder> configure = null);
    void Update(string jobName, Action<ScheduledJobOptionsBuilder> configure = null);
    void Remove<TJob>() where TJob : class, IJob;
    void Remove(string jobName);
    JobStatus[] GetJobStatus();
    JobStatus GetJobStatus(string jobName);
    Task RunJobAsync<TJob>(CancellationToken cancellationToken = default) where TJob : class, IJob;
    Task RunJobAsync(string jobName, CancellationToken cancellationToken = default);
}

public class JobManager : IJobManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICacheClient _cacheClient;
    private readonly List<ScheduledJobInstance> _jobs = [];
    private ScheduledJobInstance[] _jobsArray;
    private readonly object _lock = new();

    public JobManager(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        var cacheClient = serviceProvider.GetService<ICacheClient>();
        bool hasCacheClient = cacheClient != null;
        _cacheClient = cacheClient ?? new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory));
        _jobs.AddRange(serviceProvider.GetServices<ScheduledJobRegistration>().Select(j => new ScheduledJobInstance(j.Options, serviceProvider, _cacheClient, loggerFactory)));
        _jobsArray = _jobs.ToArray();
        if (_jobs.Any(j => j.Options.IsDistributed && !hasCacheClient))
            throw new ArgumentException("A distributed cache client is required to run distributed jobs.");
    }

    public void AddOrUpdate<TJob>(Action<ScheduledJobOptionsBuilder> configure = null) where TJob : class, IJob
    {
        string jobName = JobOptions.GetDefaultJobName(typeof(TJob));
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job == null)
            {
                var options = new ScheduledJobOptions
                {
                   Name = jobName,
                    JobFactory = sp => sp.GetRequiredService<TJob>()
                };
                var builder = new ScheduledJobOptionsBuilder(options);
                configure?.Invoke(builder);
                _jobs.Add(new ScheduledJobInstance(options, _serviceProvider, _cacheClient, _loggerFactory));
                _jobsArray = _jobs.ToArray();
            }
            else
            {
                var builder = new ScheduledJobOptionsBuilder(job.Options);
                configure?.Invoke(builder);
            }
        }
    }

    public void AddOrUpdate(string jobName, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job == null)
            {
                var options = new ScheduledJobOptions
                {
                    Name = jobName,
                };
                var builder = new ScheduledJobOptionsBuilder(options);
                configure?.Invoke(builder);
                _jobs.Add(new ScheduledJobInstance(options, _serviceProvider, _cacheClient, _loggerFactory));
                _jobsArray = _jobs.ToArray();
            }
            else
            {
                var builder = new ScheduledJobOptionsBuilder(job.Options);
                configure?.Invoke(builder);
            }
        }
    }

    public void Update<TJob>(Action<ScheduledJobOptionsBuilder> configure = null)
    {
        string jobName = JobOptions.GetDefaultJobName(typeof(TJob));
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job == null)
                throw new ArgumentException("Job not found.", nameof(jobName));

            var builder = new ScheduledJobOptionsBuilder(job.Options);
            configure?.Invoke(builder);
        }
    }

    public void Update(string jobName, Action<ScheduledJobOptionsBuilder> configure = null)
    {
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job == null)
                throw new ArgumentException("Job not found.", nameof(jobName));

            var builder = new ScheduledJobOptionsBuilder(job.Options);
            configure?.Invoke(builder);
        }
    }

    public void Remove<TJob>() where TJob : class, IJob
    {
        string jobName = JobOptions.GetDefaultJobName(typeof(TJob));
        lock (_lock)
        {
            var job = Jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
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
            var job = _jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
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
            LastDuration = j.LastDuration,
            LastErrorMessage = j.LastErrorMessage,
            NextRun = j.NextRun,
            IsRunning = j.IsRunning,
            IsEnabled = j.Options.IsEnabled
        }).ToArray();
    }

    public JobStatus GetJobStatus(string jobName) => GetJobStatus().FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Job not found.", nameof(jobName));

    public async Task RunJobAsync<TJob>(CancellationToken cancellationToken = default) where TJob : class, IJob
    {
        string jobName = JobOptions.GetDefaultJobName(typeof(TJob));
        await RunJobAsync(jobName, cancellationToken).AnyContext();
    }

    public async Task RunJobAsync(string jobName, CancellationToken cancellationToken = default)
    {
        var job = Jobs.FirstOrDefault(j => j.Options.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
        if (job == null)
            throw new ArgumentException("Job not found.", nameof(jobName));

        using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job: " + job.Options.Name);
        await job.StartAsync(true, cancellationToken).AnyContext();
    }

    internal ScheduledJobInstance[] Jobs => _jobsArray;
}

public class JobStatus
{
    public string Name { get; set; }
    public string Schedule { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? LastSuccess { get; set; }
    public TimeSpan? LastDuration { get; set; }
    public string LastErrorMessage { get; set; }
    public DateTime? NextRun { get; set; }
    public bool IsRunning { get; set; }
    public bool IsEnabled { get; set; }
}
