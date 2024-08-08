using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public class JobRunner
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private string _jobName;
    private readonly JobOptions _options;
    private readonly IServiceProvider _serviceProvider;

    public JobRunner(JobOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger<JobRunner>() ?? NullLogger<JobRunner>.Instance;
        _options = options;
        _serviceProvider = serviceProvider;
    }

    public JobRunner(IJob instance, IServiceProvider serviceProvider, ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1, bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
        : this(new JobOptions
        {
            JobFactory = _ => instance,
            InitialDelay = initialDelay,
            InstanceCount = instanceCount,
            IterationLimit = iterationLimit,
            RunContinuous = runContinuous,
            Interval = interval
        }, serviceProvider, loggerFactory)
    {
    }

    public JobRunner(Func<IServiceProvider, IJob> jobFactory, IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory = null, TimeSpan? initialDelay = null, int instanceCount = 1,
        bool runContinuous = true, int iterationLimit = -1, TimeSpan? interval = null)
        : this(new JobOptions
    {
        JobFactory = jobFactory,
        InitialDelay = initialDelay,
        InstanceCount = instanceCount,
        IterationLimit = iterationLimit,
        RunContinuous = runContinuous,
        Interval = interval
    }, serviceProvider, loggerFactory)
    {
    }

    public CancellationTokenSource CancellationTokenSource { get; private set; }

    public async Task<int> RunInConsoleAsync()
    {
        int result;
        try
        {
            CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(GetShutdownCancellationToken(_logger));
            bool success = await RunAsync(CancellationTokenSource.Token).AnyContext();
            result = success ? 0 : -1;

            if (Debugger.IsAttached)
                Console.ReadKey();
        }
        catch (TaskCanceledException)
        {
            return 0;
        }
        catch (FileNotFoundException e)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError("{Message} ({FileName})", e.GetMessage(), e.FileName);

            if (Debugger.IsAttached)
                Console.ReadKey();

            return 1;
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(e, "Job {JobName} error: {Message}", _jobName, e.GetMessage());

            if (Debugger.IsAttached)
                Console.ReadKey();

            return 1;
        }

        return result;
    }

    public void RunInBackground(CancellationToken cancellationToken = default)
    {
        if (_options.InstanceCount == 1)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(cancellationToken).AnyContext();
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError(ex, "Error running job in background: {Message}", ex.Message);
                    throw;
                }
            }, cancellationToken);
        }
        else
        {
            var ignored = RunAsync(cancellationToken);
        }
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_options.JobFactory == null)
        {
            _logger.LogError("JobFactory must be specified");
            return false;
        }

        IJob job = null;
        try
        {
            job = _options.JobFactory(_serviceProvider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating job instance from JobFactory");
            return false;
        }

        if (job == null)
        {
            _logger.LogError("JobFactory returned null job instance");
            return false;
        }

        _jobName = TypeHelper.GetTypeDisplayName(job.GetType());
        using (_logger.BeginScope(s => s.Property("job", _jobName)))
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Starting job type {JobName} on machine {MachineName}...", _jobName, Environment.MachineName);

            var jobLifetime = job as IAsyncLifetime;
            if (jobLifetime != null)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Initializing job lifetime {JobName} on machine {MachineName}...", _jobName, Environment.MachineName);
                await jobLifetime.InitializeAsync().AnyContext();
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Done initializing job lifetime {JobName} on machine {MachineName}.", _jobName, Environment.MachineName);
            }

            try
            {
                if (_options.InitialDelay.HasValue && _options.InitialDelay.Value > TimeSpan.Zero)
                    await _timeProvider.SafeDelay(_options.InitialDelay.Value, cancellationToken).AnyContext();

                if (_options.RunContinuous && _options.InstanceCount > 1)
                {
                    try
                    {
                        var tasks = new List<Task>(_options.InstanceCount);
                        for (int i = 0; i < _options.InstanceCount; i++)
                        {
                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    var jobInstance = _options.JobFactory(_serviceProvider);
                                    await jobInstance.RunContinuousAsync(_options.Interval, _options.IterationLimit,
                                        cancellationToken).AnyContext();
                                }
                                catch (TaskCanceledException)
                                {
                                }
                                catch (Exception ex)
                                {
                                    if (_logger.IsEnabled(LogLevel.Error))
                                        _logger.LogError(ex, "Error running job instance: {Message}", ex.Message);
                                    throw;
                                }
                            }, cancellationToken));
                        }

                        await Task.WhenAll(tasks).AnyContext();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
                else if (_options.RunContinuous && _options.InstanceCount == 1)
                {
                    await job.RunContinuousAsync(_options.Interval, _options.IterationLimit, cancellationToken).AnyContext();
                }
                else
                {
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    _logger.LogJobResult(result, _jobName);

                    return result.IsSuccess;
                }
            }
            finally
            {
                var jobDisposable = job as IAsyncDisposable;
                if (jobDisposable != null)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Disposing job lifetime {JobName} on machine {MachineName}...", _jobName, Environment.MachineName);
                    await jobDisposable.DisposeAsync().AnyContext();
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Done disposing job lifetime {JobName} on machine {MachineName}.", _jobName, Environment.MachineName);
                }
            }
        }

        return true;
    }

    private static CancellationTokenSource _jobShutdownCancellationTokenSource;
    private static readonly object _lock = new();
    public static CancellationToken GetShutdownCancellationToken(ILogger logger = null)
    {
        if (_jobShutdownCancellationTokenSource != null)
            return _jobShutdownCancellationTokenSource.Token;

        lock (_lock)
        {
            if (_jobShutdownCancellationTokenSource != null)
                return _jobShutdownCancellationTokenSource.Token;

            _jobShutdownCancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, args) =>
            {
                _jobShutdownCancellationTokenSource.Cancel();
                if (logger != null & logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Job shutdown event signaled: {SpecialKey}", args.SpecialKey);
                args.Cancel = true;
            };

            string webJobsShutdownFile = Environment.GetEnvironmentVariable("WEBJOBS_SHUTDOWN_FILE");
            if (String.IsNullOrEmpty(webJobsShutdownFile))
                return _jobShutdownCancellationTokenSource.Token;

            var handler = new FileSystemEventHandler((s, e) =>
            {
                if (e.FullPath.IndexOf(Path.GetFileName(webJobsShutdownFile), StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                _jobShutdownCancellationTokenSource.Cancel();
                logger?.LogInformation("Job shutdown signaled");
            });

            var watcher = new FileSystemWatcher(Path.GetDirectoryName(webJobsShutdownFile));
            watcher.Created += handler;
            watcher.Changed += handler;
            watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite;
            watcher.IncludeSubdirectories = false;
            watcher.EnableRaisingEvents = true;

            return _jobShutdownCancellationTokenSource.Token;
        }
    }
}
