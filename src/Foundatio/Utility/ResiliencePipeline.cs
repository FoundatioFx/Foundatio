using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility.Resilience;

public interface IResiliencePipelineProvider
{
    IResiliencePipeline GetPipeline(string name = null);
}

public interface IResiliencePipeline
{
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}

public interface IHaveResiliencePipelineProvider
{
    IResiliencePipelineProvider ResiliencePipelineProvider { get; }
}

public class FoundatioResiliencePipelineProvider : IResiliencePipelineProvider
{
    public static string DefaultPipelineName => "_default_";

    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IResiliencePipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private IResiliencePipeline _defaultPipeline;

    public FoundatioResiliencePipelineProvider(TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _defaultPipeline = new FoundatioResiliencePipeline(5, null, _timeProvider, _loggerFactory?.CreateLogger<FoundatioResiliencePipeline>());
    }

    public IResiliencePipelineProvider WithDefaultPipeline(IResiliencePipeline pipeline)
    {
        if (pipeline == null)
            throw new ArgumentNullException(nameof(pipeline));

        _defaultPipeline = pipeline;
        return this;
    }

    public IResiliencePipelineProvider WithPipeline(string name, IResiliencePipeline pipeline)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (pipeline == null)
            throw new ArgumentNullException(nameof(pipeline));

        _pipelines[name] = pipeline;
        return this;
    }

    public IResiliencePipeline GetPipeline(string name = null)
    {
        if (name == null)
            return _defaultPipeline;

        return _pipelines.GetOrAdd(name, _ => _defaultPipeline);
    }
}

public class FoundatioResiliencePipeline : IResiliencePipeline
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public FoundatioResiliencePipeline(TimeProvider timeProvider = null, ILogger logger = null) : this(5, null, timeProvider, logger)
    {
    }

    public FoundatioResiliencePipeline(int maxAttempts = 5, TimeSpan? retryInterval = null,TimeProvider timeProvider = null, ILogger logger = null)
    {
        MaxAttempts = maxAttempts;
        RetryInterval = retryInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FoundatioResiliencePipeline>.Instance;
    }

    public TimeSpan? RetryInterval { get; set; }
    public int MaxAttempts { get; set; }

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        int currentBackoffTime = _defaultBackoffIntervals[0];
        if (RetryInterval != null)
            currentBackoffTime = (int)RetryInterval.Value.TotalMilliseconds;

        do
        {
            if (attempts > 1 && _logger != null)
                _logger.LogInformation("Retrying {Attempts} attempt after {Delay:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

            try
            {
                await action(cancellationToken).AnyContext();
                return;
            }
            catch (Exception ex)
            {
                if (attempts >= MaxAttempts)
                    throw;

                if (_logger != null)
                    _logger.LogError(ex, "Retry error: {Message}", ex.Message);

                await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(currentBackoffTime), cancellationToken).AnyContext();
            }

            if (RetryInterval == null)
                currentBackoffTime = _defaultBackoffIntervals[Math.Min(attempts, _defaultBackoffIntervals.Length - 1)];
            attempts++;
        } while (attempts <= MaxAttempts && !cancellationToken.IsCancellationRequested);

        throw new TaskCanceledException("Should not get here");
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        int currentBackoffTime = _defaultBackoffIntervals[0];
        if (RetryInterval != null)
            currentBackoffTime = (int)RetryInterval.Value.TotalMilliseconds;

        do
        {
            if (attempts > 1 && _logger != null)
                _logger.LogInformation("Retrying {Attempts} attempt after {Delay:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

            try
            {
                return await action(cancellationToken).AnyContext();
            }
            catch (Exception ex)
            {
                if (attempts >= MaxAttempts)
                    throw;

                if (_logger != null)
                    _logger.LogError(ex, "Retry error: {Message}", ex.Message);

                await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(currentBackoffTime), cancellationToken).AnyContext();
            }

            if (RetryInterval == null)
                currentBackoffTime = _defaultBackoffIntervals[Math.Min(attempts, _defaultBackoffIntervals.Length - 1)];
            attempts++;
        } while (attempts <= MaxAttempts && !cancellationToken.IsCancellationRequested);

        throw new TaskCanceledException("Should not get here");
    }

    private static readonly int[] _defaultBackoffIntervals = [100, 1000, 2000, 2000, 5000, 5000, 10000, 30000, 60000];
}

public static class FoundatioResiliencePipelineExtensions
{
    public static IResiliencePipelineProvider GetResiliencePipelineProvider(this object target)
    {
        return target is IHaveResiliencePipelineProvider accessor ? accessor.ResiliencePipelineProvider : null;
    }

    public static ValueTask ExecuteAsync(this IResiliencePipeline pipeline, Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (pipeline == null)
            throw new ArgumentNullException(nameof(pipeline));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return pipeline.ExecuteAsync(_ => action(), cancellationToken);
    }

    public static ValueTask<T> ExecuteAsync<T>(this IResiliencePipeline pipeline, Func<ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (pipeline == null)
            throw new ArgumentNullException(nameof(pipeline));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return pipeline.ExecuteAsync(_ => action(), cancellationToken);
    }
}
